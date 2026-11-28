#include <errno.h>
#include <stdio.h>
#include <wasi.h>
#include <wasmtime.h>

enum { GUEST_FAILED = 1, HOST_FAILED = 2, OUTPUT_LIMIT = 3, EXECUTION_LIMIT = 4 };
enum { MAX_OUTPUT = 16 * 1024, MAX_MEMORY = 96 * 1024 * 1024 };
enum { WASM_STACK = 8 * 1024 * 1024, HOST_STACK = 512 * 1024 };
static const uint64_t fuel = UINT64_C(4000000000);
static const char bootstrap[] =
    "import io, json, sys\n"
    "request = json.load(sys.stdin)\n"
    "if request.get('workspace', False):\n"
    "    import tempfile\n"
    "    tempfile.tempdir = '/work'\n"
    "if request.get('audit', False):\n"
    "    from aegis_script_audit import shape\n"
    "    print('AEGIS_SCRIPT_SHAPE:' + json.dumps(shape(request['code'])), file=sys.stderr)\n"
    "data = request['input']\n"
    "sys.stdin = io.TextIOWrapper(io.BytesIO((data or '').encode('utf-8')), encoding='utf-8')\n"
    "exec(compile(request['code'], '<aegis-script>', 'exec'), "
    "{'__name__': '__main__', 'input_data': data})\n";

struct capture {
    FILE *stream;
    size_t remaining;
    bool exceeded;
};

static ptrdiff_t write_output(void *data, const unsigned char *bytes, size_t size) {
    struct capture *capture = data;
    if (size > capture->remaining) {
        capture->exceeded = true;
        return -EFBIG;
    }
    if (fwrite(bytes, 1, size, capture->stream) != size) {
        return -EIO;
    }
    capture->remaining -= size;
    return (ptrdiff_t)size;
}

static int runtime_failed(const char *step, wasmtime_error_t *error) {
    fprintf(stderr, "Python runtime could not %s.\n", step);
    if (error != NULL) {
        wasm_name_t detail;
        wasmtime_error_message(error, &detail);
        fwrite(detail.data, 1, detail.size, stderr);
        fputc('\n', stderr);
        wasm_name_delete(&detail);
        wasmtime_error_delete(error);
    }
    return HOST_FAILED;
}

int main(int argc, char **paths) {
    if (argc != 1 && argc != 4) return HOST_FAILED;
    wasm_config_t *config = wasm_config_new();
    wasmtime_config_consume_fuel_set(config, true);
    wasmtime_config_max_wasm_stack_set(config, WASM_STACK);
    wasmtime_config_async_stack_size_set(config, WASM_STACK + HOST_STACK);
    wasm_engine_t *engine = wasm_engine_new_with_config(config);
    if (engine == NULL) {
        return runtime_failed("initialize the engine", NULL);
    }

    wasmtime_module_t *module;
    /* The precompiled module is shipped in the image. Scripts only enter through stdin. */
    wasmtime_error_t *error = wasmtime_module_deserialize_file(
        engine, "/opt/aegis-python/python.cwasm", &module);
    if (error != NULL) {
        return runtime_failed("load Python", error);
    }

    wasmtime_store_t *store = wasmtime_store_new(engine, NULL, NULL);
    wasmtime_store_limiter(store, MAX_MEMORY, -1, 1, -1, 1);
    wasmtime_context_t *context = wasmtime_store_context(store);
    error = wasmtime_context_set_fuel(context, argc == 4 ? 4 * fuel : fuel);
    if (error != NULL) {
        return runtime_failed("set the execution limit", error);
    }

    struct capture out = {stdout, MAX_OUTPUT, false};
    struct capture err = {stderr, MAX_OUTPUT, false};
    wasi_config_t *wasi = wasi_config_new();
    const char *argv[] = {"/usr/local/bin/python3.14", "-I", "-S", "-B", "-u", "-c", bootstrap};
    if (!wasi_config_set_argv(wasi, sizeof(argv) / sizeof(argv[0]), argv)
        || !wasi_config_preopen_dir(wasi, "/opt/aegis-python/stdlib", "/usr/local/lib/python3.14", false)) {
        wasi_config_delete(wasi);
        return runtime_failed("configure the standard library", NULL);
    }
    if (argc == 4 && (!wasi_config_preopen_dir(wasi, paths[1], "/inputs", false)
        || !wasi_config_preopen_dir(wasi, paths[2], "/work", true)
        || !wasi_config_preopen_dir(wasi, paths[3], "/outputs", true))) {
        wasi_config_delete(wasi);
        return runtime_failed("configure conversation files", NULL);
    }
    wasi_config_inherit_stdin(wasi);
    wasi_config_set_stdout_custom(wasi, write_output, &out, NULL);
    wasi_config_set_stderr_custom(wasi, write_output, &err, NULL);
    error = wasmtime_context_set_wasi(context, wasi);
    if (error != NULL) {
        return runtime_failed("configure WASI", error);
    }

    wasmtime_linker_t *linker = wasmtime_linker_new(engine);
    error = wasmtime_linker_define_wasi(linker);
    if (error != NULL) {
        return runtime_failed("link WASI", error);
    }
    wasm_trap_t *trap = NULL;
    wasmtime_instance_t instance;
    error = wasmtime_linker_instantiate(linker, context, module, &instance, &trap);
    if (error != NULL || trap != NULL) {
        return runtime_failed("instantiate Python", error);
    }

    wasmtime_extern_t start;
    if (!wasmtime_instance_export_get(context, &instance, "_start", 6, &start)
        || start.kind != WASMTIME_EXTERN_FUNC) {
        return runtime_failed("find Python's entry point", NULL);
    }
    error = wasmtime_func_call(context, &start.of.func, NULL, 0, NULL, 0, &trap);
    int result = 0;
    if (out.exceeded || err.exceeded) {
        result = OUTPUT_LIMIT;
    } else if (trap != NULL) {
        wasmtime_trap_code_t code;
        result = wasmtime_trap_code(trap, &code) && code == WASMTIME_TRAP_CODE_OUT_OF_FUEL
            ? EXECUTION_LIMIT : GUEST_FAILED;
    } else if (error != NULL) {
        int status;
        result = wasmtime_error_exit_status(error, &status) && status == 0 ? 0 : GUEST_FAILED;
    }

    if (trap != NULL) {
        wasm_trap_delete(trap);
    }
    if (error != NULL) {
        wasmtime_error_delete(error);
    }
    wasmtime_linker_delete(linker);
    wasmtime_store_delete(store);
    wasmtime_module_delete(module);
    wasm_engine_delete(engine);
    return result;
}
