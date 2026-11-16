package com.aegis.node

import ai.onnxruntime.OnnxTensor
import ai.onnxruntime.OrtEnvironment
import ai.onnxruntime.OrtSession
import java.nio.ByteBuffer
import java.nio.ByteOrder
import java.nio.FloatBuffer

/** Bounded second-stage verifier. Called only after a microWakeWord candidate.
 * Models and deterministic noise state are imported privately, never APK assets.
 * This class opens no microphone, network connection, timer or background thread.
 */
internal class HandsFreeWakeVerifier(
    melModel:ByteArray,
    embeddingModel:ByteArray,
    keywordModel:ByteArray,
    seedEmbedding:ByteArray
):AutoCloseable {
    companion object {
        const val THRESHOLD=0.5f
        const val PRE_SAMPLES=40000
        const val POST_SAMPLES=2560
        const val BLOCK_SAMPLES=1280
        const val SCORE_LOOKBACK_SAMPLES=7680
        const val MAX_INPUT_SAMPLES=PRE_SAMPLES+POST_SAMPLES
        const val SEED_BYTES=16*96*4

        internal fun decodeInitialState(bytes:ByteArray):FloatArray {
            require(bytes.size==SEED_BYTES){"wake_verifier_invalid_seed"}
            val buffer=ByteBuffer.wrap(bytes).order(ByteOrder.LITTLE_ENDIAN).asFloatBuffer()
            return FloatArray(16*96).also { buffer.get(it);require(it.all(Float::isFinite)){"wake_verifier_invalid_seed"} }
        }
        internal fun validateWindow(size:Int,count:Int,candidateSample:Int) {
            require(count in 0..minOf(size,MAX_INPUT_SAMPLES) && candidateSample in 0..count){"wake_verifier_invalid_window"}
        }
        internal fun maximumScore(scores:FloatArray,candidateSample:Int):Float {
            var maximum=0f
            for(i in scores.indices) {
                require(scores[i].isFinite() && scores[i] in 0f..1f){"wake_verifier_invalid_score"}
                val at=(i+1)*BLOCK_SAMPLES
                if(at>=candidateSample-SCORE_LOOKBACK_SAMPLES && at<=candidateSample+POST_SAMPLES)
                    maximum=maxOf(maximum,scores[i])
            }
            return maximum
        }
    }

    private val initialState=decodeInitialState(seedEmbedding)
    private val environment=OrtEnvironment.getEnvironment()
    // Options must outlive all sessions using them (ORT Java API contract).
    private val options=OrtSession.SessionOptions()
    private val sessions=ArrayList<OrtSession>(3)
    private var closed=false

    init {
        try {
            require(melModel.size in 1..2_000_000 && embeddingModel.size in 1..2_000_000 && keywordModel.size in 1..1_000_000){"wake_verifier_invalid_model_size"}
            environment.setTelemetry(false)
            options.setIntraOpNumThreads(1)
            options.setInterOpNumThreads(1)
            options.setExecutionMode(OrtSession.SessionOptions.ExecutionMode.SEQUENTIAL)
            options.addConfigEntry("session.intra_op.allow_spinning","0")
            options.addConfigEntry("session.inter_op.allow_spinning","0")
            sessions.add(environment.createSession(melModel,options))
            sessions.add(environment.createSession(embeddingModel,options))
            sessions.add(environment.createSession(keywordModel,options))
            require(sessions[0].inputNames==setOf("input") && sessions[1].inputNames==setOf("input_1") && sessions[2].inputNames==setOf("features")){"wake_verifier_invalid_graph"}
        } catch(error:Throwable) {
            runCatching { close() }
            throw error
        }
    }

    /** candidateSample is relative to samples[0], not the capture's absolute clock.
     * No leading/trailing padding: the last incomplete80ms block is not processed.
     * A failed or cancelled inference throws; callers must not convert it to accept.
     */
    @Synchronized
    fun score(samples:ShortArray,count:Int,candidateSample:Int,
              cancelled:()->Boolean={Thread.currentThread().isInterrupted}):Float {
        check(!closed){"wake_verifier_closed"}
        validateWindow(samples.size,count,candidateSample)
        return maximumScore(frameScoresLocked(samples,count,cancelled),candidateSample)
    }

    /** Import-time graph and output-contract check, before replacing private files. */
    fun validate() { score(ShortArray(BLOCK_SAMPLES),BLOCK_SAMPLES,BLOCK_SAMPLES) }

    @Synchronized
    internal fun frameScores(samples:ShortArray,count:Int=samples.size):FloatArray {
        check(!closed){"wake_verifier_closed"}
        validateWindow(samples.size,count,count)
        return frameScoresLocked(samples,count){Thread.currentThread().isInterrupted}
    }

    private fun frameScoresLocked(samples:ShortArray,count:Int,cancelled:()->Boolean):FloatArray {
        if(cancelled())throw InterruptedException("wake_verifier_cancelled")
        val melHistory=FloatArray(76*32){1f}
        val featureHistory=initialState.copyOf()
        val scores=FloatArray(count/BLOCK_SAMPLES)
        for(frame in scores.indices) {
            if(cancelled())throw InterruptedException("wake_verifier_cancelled")
            val end=(frame+1)*BLOCK_SAMPLES
            val start=maxOf(0,end-BLOCK_SAMPLES-480)
            // The upstream model expects raw signed16-bit amplitudes as float.
            val raw=FloatArray(end-start){samples[start+it].toFloat()}
            val mel=run(sessions[0],"input",raw,longArrayOf(1,raw.size.toLong()))
            require(mel.isNotEmpty() && mel.size%32==0 && mel.size<=8*32){"wake_verifier_invalid_mel_shape"}
            for(i in mel.indices)mel[i]=mel[i]/10f+2f
            append(melHistory,mel)
            val embedding=run(sessions[1],"input_1",melHistory,longArrayOf(1,76,32,1))
            require(embedding.size==96){"wake_verifier_invalid_embedding_shape"}
            append(featureHistory,embedding)
            val prediction=run(sessions[2],"features",featureHistory,longArrayOf(1,16,96))
            require(prediction.size==1 && prediction[0] in 0f..1f){"wake_verifier_invalid_prediction"}
            // Upstream suppresses the first five model predictions after reset.
            scores[frame]=if(frame<5)0f else prediction[0]
        }
        return scores
    }

    private fun append(history:FloatArray,values:FloatArray) {
        require(values.size<=history.size){"wake_verifier_history_overflow"}
        System.arraycopy(history,values.size,history,0,history.size-values.size)
        System.arraycopy(values,0,history,history.size-values.size,values.size)
    }

    private fun run(session:OrtSession,inputName:String,values:FloatArray,shape:LongArray):FloatArray {
        return OnnxTensor.createTensor(environment,FloatBuffer.wrap(values),shape).use { input->
            session.run(mapOf(inputName to input)).use { result->
                val tensor=result[0] as? OnnxTensor ?: error("wake_verifier_invalid_output")
                val buffer=tensor.floatBuffer ?: error("wake_verifier_invalid_output")
                FloatArray(buffer.remaining()).also { output->
                    buffer.get(output)
                    require(output.all(Float::isFinite)){"wake_verifier_nonfinite_output"}
                }
            }
        }
    }

    @Synchronized
    override fun close() {
        if(closed)return
        closed=true
        var failure:Throwable?=null
        for(session in sessions.asReversed()) {
            try { session.close() } catch(error:Throwable) { if(failure==null)failure=error }
        }
        sessions.clear()
        try { options.close() } catch(error:Throwable) { if(failure==null)failure=error }
        initialState.fill(0f)
        // OrtEnvironment is a process-wide singleton, not owned by this verifier.
        failure?.let { throw it }
    }
}
