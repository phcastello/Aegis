package com.aegis.node

import android.content.Context
import org.json.JSONArray
import org.json.JSONObject
import java.io.File
import java.io.FileOutputStream
import java.io.OutputStream
import java.io.RandomAccessFile
import java.security.MessageDigest
import java.time.OffsetDateTime
import java.time.format.DateTimeFormatter
import java.util.UUID
import java.util.concurrent.ArrayBlockingQueue
import java.util.concurrent.Callable
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.FutureTask
import java.util.concurrent.ThreadPoolExecutor
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicLong
import java.util.concurrent.atomic.AtomicBoolean
import java.util.zip.ZipEntry
import java.util.zip.ZipOutputStream

/** Local diagnostic audio. Its only output is an explicitly requested SAF export.
 * Capture only copies bounded PCM and enqueues work; all filesystem operations run on one writer.
 */
internal class HandsFreeActivationCollection internal constructor(
    private val directory:File,
    initialEnabled:Boolean=false,
    initialLimitMiB:Int=100,
    private val saveSettings:(Boolean,Int)->Unit={_,_->},
    private val asset:(String)->ByteArray={ByteArray(0)},
    private val now:()->OffsetDateTime={OffsetDateTime.now()}
) {
    constructor(context:Context):this(
        File(context.noBackupFilesDir,"hands-free-activations-v1"),
        context.getSharedPreferences("aegis-activation-collection-v1",Context.MODE_PRIVATE).getBoolean("enabled",false),
        context.getSharedPreferences("aegis-activation-collection-v1",Context.MODE_PRIVATE).getInt("limitMiB",100),
        {enabled,limit->check(context.getSharedPreferences("aegis-activation-collection-v1",Context.MODE_PRIVATE).edit().putBoolean("enabled",enabled).putInt("limitMiB",limit).commit())},
        {name->context.assets.open("hands-free-reviewer/$name").use{it.readBytes()}}
    )
    @Volatile private var enabled=initialEnabled
    @Volatile private var limitMiB=initialLimitMiB.coerceIn(10,2048)
    @Volatile private var problem:String?=null
    @Volatile private var ready=false
    private val statusDirty=AtomicBoolean(false)
    private val bytes=AtomicLong(0)
    private val skipped=AtomicLong(0)
    private val reservations=AtomicLong(0)
    private val audioGeneration=AtomicLong(0)
    private val acceptingAudio=ConcurrentHashMap<String,Long>()
    private val captureOpen=ConcurrentHashMap.newKeySet<String>()
    private val pendingIssues=ConcurrentHashMap<String,String>()
    private val pendingCaptureDetails=ConcurrentHashMap<String,JSONObject>()
    private val pendingCaptureClose=ConcurrentHashMap.newKeySet<String>()
    private val pendingFinishes=ConcurrentHashMap<String,JSONObject>()
    private fun recordProblem(code:String){problem=code;statusDirty.set(true)}
    private fun clearProblem(){problem=null;statusDirty.set(true)}
    private fun persistStatusIfNeeded() {
        if(!statusDirty.compareAndSet(true,false))return
        try{atomicJson(File(directory,"status.json"),JSONObject().put("skippedEvents",skipped.get()).put("problem",problem?:JSONObject.NULL))}
        catch(_:Throwable){recordProblem("storage_failure");statusDirty.set(true)}
    }
    private val sessions=linkedMapOf<String,Record>()
    private val executor=ThreadPoolExecutor(1,1,0,TimeUnit.MILLISECONDS,ArrayBlockingQueue<Runnable>(256),{r->Thread(r,"AegisActivationStore").apply{isDaemon=true}},ThreadPoolExecutor.AbortPolicy())
    private data class Record(val folder:File,val metadata:JSONObject,var contextSamples:Int=0,var postSamples:Int=0,var captureClosed:Boolean=false)
    init { enqueue { recover();ready=true;if(problem=="storage_starting")clearProblem() } }
    fun state():JSONObject=JSONObject().put("enabled",enabled).put("limitMiB",limitMiB).put("bytes",bytes.get()).put("ready",ready)
        .put("status",problem?:if(ready)"ready" else "starting").put("skippedEvents",skipped.get())
    fun configure(value:Boolean,limit:Int) {
        require(limit in 10..2048);saveSettings(value,limit);enabled=value;limitMiB=limit
        if(!value) {
            audioGeneration.incrementAndGet()
            val at=now().toString();val ids=captureOpen.toList();captureOpen.removeAll(ids.toSet())
            for(id in ids){pendingCaptureDetails[id]=JSONObject().put("captureEndReason","collection_disabled").put("captureEndedAt",at);pendingCaptureClose.add(id)}
            enqueue {}
        }
        if(problem=="storage_limit" && bytes.get()+reservations.get()+EVENT_RESERVATION<limit.toLong()*1024*1024)clearProblem()
        enqueue {}
    }
    /** metadata is sanitized by the caller: no transcript, backend credentials or device identity. */
    fun begin(metadata:JSONObject,triggerContext:ShortArray):String? {
        val generation=audioGeneration.get()
        if(!enabled)return null
        if(!ready){skipped.incrementAndGet();recordProblem("storage_starting");enqueue {};return null}
        if(bytes.get()+reservations.addAndGet(EVENT_RESERVATION)>limitMiB.toLong()*1024*1024){reservations.addAndGet(-EVENT_RESERVATION);skipped.incrementAndGet();recordProblem("storage_limit");enqueue {};return null}
        val id=UUID.randomUUID().toString();val at=now()
        val row=JSONObject(metadata.toString()).put("schemaVersion",1).put("id",id).put("activatedAt",metadata.opt("activatedAt")?:at.toString())
            .put("endedAt",JSONObject.NULL).put("finalState","capturing").put("endReason",JSONObject.NULL).put("transcriptionStatus","not_started")
            .put("humanLabel",JSONObject.NULL).put("triggerContext",JSONObject.NULL).put("postActivation",JSONObject.NULL).put("storageIssues",JSONArray()).put("captureIssues",JSONArray())
        for(key in listOf("appVersion","build","modelVersion","modelSha256","detectorConfig","instantaneousScore","decisionScore","origin","initialScreenInteractive","initialDeviceLocked","groupId","sessionId"))if(!row.has(key))row.put(key,JSONObject.NULL)
        val context=triggerContext.copyOfRange((triggerContext.size-MAX_CONTEXT_SAMPLES).coerceAtLeast(0),triggerContext.size)
        acceptingAudio[id]=generation;captureOpen.add(id)
        if(!enqueue {
            val folder=File(directory,"${at.toLocalDate()}/$id")
            val record=Record(folder,row)
            try {
                check(folder.mkdirs() || folder.isDirectory);sessions[id]=record
                atomicJson(File(folder,"event.json"),row)
                writeWav(File(folder,CONTEXT),ShortArray(0));writeWav(File(folder,POST),ShortArray(0))
                append(record,CONTEXT,context,MAX_CONTEXT_SAMPLES);checkpoint(record)
            }catch(_:Throwable){
                recordProblem("storage_failure");row.getJSONArray("storageIssues").put("event_initialization_failed")
                try{atomicJson(File(folder,"event.json"),row)}catch(_:Throwable){skipped.incrementAndGet();statusDirty.set(true);sessions.remove(id);acceptingAudio.remove(id);captureOpen.remove(id);reservations.addAndGet(-EVENT_RESERVATION)}
            }finally{context.fill(0)}
        }){context.fill(0);reservations.addAndGet(-EVENT_RESERVATION);skipped.incrementAndGet();statusDirty.set(true);acceptingAudio.remove(id);captureOpen.remove(id);enqueue {};return null}
        return id
    }
    fun setTriggerContext(id:String?,pcm:ShortArray) {
        if(id==null || !enabled || !captureOpen.contains(id) || acceptingAudio[id]!=audioGeneration.get())return
        val copy=pcm.copyOfRange((pcm.size-MAX_CONTEXT_SAMPLES).coerceAtLeast(0),pcm.size)
        if(!enqueue {sessions[id]?.let{record->if(!record.captureClosed && record.contextSamples==0)append(record,CONTEXT,copy,MAX_CONTEXT_SAMPLES)};copy.fill(0)}){copy.fill(0);pendingIssues[id]="persistence_queue_full"}
    }
    fun appendPost(id:String?,pcm:ShortArray,offset:Int=0,count:Int=pcm.size-offset)=appendQueued(id,POST,pcm,offset,count,MAX_POST_SAMPLES)
    fun appendContext(id:String?,pcm:ShortArray,offset:Int=0,count:Int=pcm.size-offset)=appendQueued(id,CONTEXT,pcm,offset,count,MAX_CONTEXT_SAMPLES)
    private fun appendQueued(id:String?,name:String,pcm:ShortArray,offset:Int,count:Int,limit:Int) {
        if(id==null || count<=0 || !enabled || !captureOpen.contains(id) || acceptingAudio[id]!=audioGeneration.get())return
        require(offset>=0 && count<=pcm.size-offset)
        val copied=pcm.copyOfRange(offset,offset+minOf(count,3200))
        if(!enqueue {try{sessions[id]?.let{if(!it.captureClosed)try{append(it,name,copied,limit)}catch(_:Throwable){addIssue(it.metadata,"storageIssues","wav_write_failed");recordProblem("storage_failure")}}}finally{copied.fill(0)}}) {copied.fill(0);pendingIssues[id]="persistence_queue_full"}
    }
    fun finishCapture(id:String?) {if(id==null)return;captureOpen.remove(id);if(!enqueue {sessions[id]?.let{it.captureClosed=true;checkpoint(it)}}){pendingCaptureClose.add(id);pendingIssues[id]="persistence_queue_full";enqueue {}}}
    fun finish(id:String?,finalState:String,reason:String?,transcriptionStatus:String="not_started",extra:JSONObject?=null) {
        if(id==null)return
        captureOpen.remove(id)
        val result=JSONObject().put("finalState",finalState).put("endReason",reason?:JSONObject.NULL).put("endedAt",now().toString()).put("transcriptionStatus",transcriptionStatus)
        if(extra!=null)for(key in extra.keys())result.put(key,extra.get(key))
        if(!enqueue {complete(id,result)}){pendingFinishes[id]=result;enqueue {}}
    }
    private fun enqueue(task:()->Unit):Boolean=try {executor.execute{try{task()}catch(_:Throwable){recordProblem("storage_failure")}finally{drainPending();persistStatusIfNeeded()}};true}catch(_:java.util.concurrent.RejectedExecutionException){recordProblem("persistence_queue_full");false}
    private fun drainPending() {
        for((id,issue) in pendingIssues.entries.toList())sessions[id]?.let {record->addIssue(record.metadata,"captureIssues",issue);pendingIssues.remove(id)}
        // Fallback controls execute only after all PCM queued before them has drained.
        if(executor.queue.isNotEmpty())return
        for(id in pendingCaptureClose.toList())sessions[id]?.let{record->
            record.captureClosed=true
            pendingCaptureDetails.remove(id)?.let{details->for(key in details.keys())record.metadata.put(key,details.get(key))}
            checkpoint(record);pendingCaptureClose.remove(id)
        }
        for((id,result) in pendingFinishes.entries.toList())complete(id,result)
    }
    private fun complete(id:String,result:JSONObject) {
        val record=sessions[id]?:return
        try {
            record.captureClosed=true
            pendingCaptureDetails.remove(id)?.let{details->for(key in details.keys())record.metadata.put(key,details.get(key))}
            for(key in result.keys())record.metadata.put(key,result.get(key))
            checkpoint(record);sessions.remove(id);acceptingAudio.remove(id);captureOpen.remove(id);reservations.addAndGet(-EVENT_RESERVATION);pendingFinishes.remove(id);pendingCaptureClose.remove(id);pendingCaptureDetails.remove(id)
            bytes.set(directory.walkTopDown().filter{it.isFile}.sumOf{if(it.name=="status.json")0L else it.length()})
        }catch(_:Throwable){addIssue(record.metadata,"storageIssues","finalization_failed");recordProblem("storage_failure");pendingFinishes[id]=result}
    }
    private fun append(record:Record,name:String,pcm:ShortArray,limit:Int) {
        val count=if(name==POST)record.postSamples else record.contextSamples
        val take=minOf(pcm.size,(limit-count).coerceAtLeast(0))
        if(take<pcm.size && name==POST && !record.metadata.optBoolean("postLimitReached"))record.metadata.put("postLimitReached",true)
        if(take==0)return
        val data=ByteArray(take*2)
        for(i in 0 until take){data[i*2]=pcm[i].toByte();data[i*2+1]=(pcm[i].toInt() shr 8).toByte()}
        FileOutputStream(File(record.folder,name),true).use{it.write(data)}
        data.fill(0);pcm.fill(0)
        if(name==POST)record.postSamples+=take else record.contextSamples+=take
        // Keep RIFF sizes usable after an abrupt process death. The writer never runs on capture.
        if(patchWav(File(record.folder,name)))addIssue(record.metadata,"storageIssues","wav_header_repaired")
    }
    private fun checkpoint(record:Record) {
        for(name in listOf(CONTEXT,POST)) {
            val file=File(record.folder,name)
            if(file.exists()){if(patchWav(file))addIssue(record.metadata,"storageIssues","wav_header_repaired");record.metadata.put(if(name==CONTEXT)"triggerContext" else "postActivation",audioInfo(file,name))}
            else addIssue(record.metadata,"storageIssues","missing_$name")
        }
        record.metadata.put("captureClosed",record.captureClosed)
        atomicJson(File(record.folder,"event.json"),record.metadata)
    }
    private fun recover() {
        directory.mkdirs()
        // Staging copies only: originals live under directory and are never purged.
        directory.parentFile?.listFiles()?.filter{it.isDirectory && it.name.startsWith("activation-export-")}?.forEach{it.deleteRecursively()}
        val statusFile=File(directory,"status.json")
        if(statusFile.exists())try{val saved=JSONObject(statusFile.readText());skipped.set(saved.optLong("skippedEvents"));if(!saved.isNull("problem"))problem=saved.optString("problem").takeIf{it.matches(Regex("[a-z_]{1,64}"))}}catch(_:Throwable){recordProblem("storage_recovery_failure")}
        for(file in directory.walkTopDown().filter{it.name=="event.json"}.toList())try {
            val row=JSONObject(file.readText())
            if((row.isNull("endedAt") || !row.has("endedAt")) && !row.optBoolean("terminalRecovered")) {
                val record=Record(file.parentFile!!,row,captureClosed=true)
                row.put("endedAt",JSONObject.NULL).put("recoveredAt",now().toString()).put("terminalRecovered",true).put("finalState","interrupted").put("endReason","process_restart_unknown").put("interruptionAt",JSONObject.NULL)
                if(!row.has("captureIssues"))row.put("captureIssues",JSONArray())
                if(!row.has("storageIssues"))row.put("storageIssues",JSONArray())
                checkpoint(record)
            }
        }catch(_:Throwable){recordProblem("storage_recovery_failure")}
        bytes.set(directory.walkTopDown().filter{it.isFile}.sumOf{if(it.name=="status.json")0L else it.length()})
        if(bytes.get()+EVENT_RESERVATION>limitMiB.toLong()*1024*1024)recordProblem("storage_limit")
    }
    private fun <T> barrier(task:()->T):T {
        val future=FutureTask(Callable{task()})
        // Only non-capture callers wait. Avoid dropping export/delete when the bounded writer is busy.
        while(true)try{executor.execute(future);break}catch(_:java.util.concurrent.RejectedExecutionException){Thread.sleep(5)}
        return future.get(60,TimeUnit.SECONDS)
    }
    fun deleteAll()=barrier {
        check(sessions.isEmpty()){ "capture_active" }
        check(directory.deleteRecursively());directory.mkdirs();bytes.set(0);skipped.set(0);clearProblem();persistStatusIfNeeded()
    }
    /** Queue barrier captures metadata + immutable copies of currently growing files. ZIP compression is outside capture/writer. */
    fun export(output:OutputStream,metricsJson:String)=export(output){metricsJson}
    fun export(output:OutputStream,metricsJson:()->String) {
        data class Snapshot(val rows:List<JSONObject>,val files:List<Pair<String,File>>,val scratch:File,val at:String,val metrics:String)
        val snapshot=barrier {
            drainPending();sessions.values.forEach{checkpoint(it)}
            val scratch=File(directory.parentFile,"activation-export-${UUID.randomUUID()}");check(scratch.mkdirs())
            try {
            val at=now().toString();val rows=mutableListOf<JSONObject>();val files=mutableListOf<Pair<String,File>>()
            for(file in directory.walkTopDown().filter{it.name=="event.json"}.sortedBy{it.path}.toList()) {
                val row=JSONObject(file.readText());val relative=file.parentFile!!.relativeTo(directory).invariantSeparatorsPath
                row.put("exportedAt",at).put("eventPath","events/$relative/event.json").put("snapshotInProgress",row.isNull("endedAt") && !row.optBoolean("terminalRecovered"))
                for((name,key) in listOf(CONTEXT to "triggerContext",POST to "postActivation")) {
                    val source=File(file.parentFile,name)
                    if(source.exists()){
                        val copy=File(scratch,"$relative/$name");copy.parentFile!!.mkdirs();source.copyTo(copy)
                        row.put(key,audioInfo(copy,"events/$relative/$name"));files.add("events/$relative/$name" to copy)
                    }else{row.put(key,JSONObject.NULL);addIssue(row,"storageIssues","missing_$name")}
                }
                rows.add(row)
            }
            Snapshot(rows,files,scratch,at,metricsJson())
            }catch(error:Throwable){scratch.deleteRecursively();throw error}
        }
        try {ZipOutputStream(output).use{zip->
            fun entry(name:String,data:ByteArray){zip.putNextEntry(ZipEntry(name));zip.write(data);zip.closeEntry()}
            entry("metrics.json",snapshot.metrics.toByteArray(Charsets.UTF_8))
            entry("manifest.jsonl",snapshot.rows.joinToString("\n",postfix=if(snapshot.rows.isEmpty())"" else "\n"){it.toString()}.toByteArray(Charsets.UTF_8))
            entry("export.json",JSONObject().put("schemaVersion",1).put("exportedAt",snapshot.at).put("metricsSnapshotAt",snapshot.at).put("eventCount",snapshot.rows.size).put("skippedEvents",skipped.get()).put("storageStatus",problem?:"ready").toString(2).toByteArray())
            for(row in snapshot.rows)entry(row.getString("eventPath"),row.toString(2).toByteArray(Charsets.UTF_8))
            for((name,file) in snapshot.files){zip.putNextEntry(ZipEntry(name));file.inputStream().use{it.copyTo(zip)};zip.closeEntry()}
            for(name in listOf("index.html","reviewer.js","reviewer.css","README.txt"))entry(name,asset(name))
            // Classic local script avoids file:// fetch restrictions and never contains untrusted executable text.
            val encoded=java.util.Base64.getEncoder().encodeToString(JSONArray(snapshot.rows).toString().toByteArray(Charsets.UTF_8))
            entry("events.js","window.AEGIS_EVENTS_BASE64='$encoded';".toByteArray(Charsets.US_ASCII))
        }}finally{snapshot.scratch.deleteRecursively()}
    }
    internal fun awaitIdle()=barrier{drainPending()}
    companion object {
        const val CONTEXT="trigger_context.wav";const val POST="post_activation.wav"
        private const val MAX_CONTEXT_SAMPLES=35200 // 2 seconds before + 200ms after accepted decision
        private const val MAX_POST_SAMPLES=320000 // same 20-second upper bound as normal capture
        private const val EVENT_RESERVATION=800000L
        private fun addIssue(row:JSONObject,key:String,code:String){
            val list=row.optJSONArray(key)?:JSONArray().also{row.put(key,it)}
            if(list.length()<16 && (0 until list.length()).none{list.optString(it)==code})list.put(code)
        }
        fun exportName()="aegis-handsfree-${OffsetDateTime.now().format(DateTimeFormatter.ofPattern("yyyy-MM-dd_HH-mm-ssxx"))}.zip"
        private fun atomicJson(file:File,row:JSONObject){val temp=File(file.parentFile,file.name+".tmp");FileOutputStream(temp).use{it.write(row.toString(2).toByteArray(Charsets.UTF_8));it.fd.sync()};check(temp.renameTo(file))}
        private fun writeWav(file:File,pcm:ShortArray){FileOutputStream(file).use{it.write(handsFreeWav(pcm,pcm.size));it.fd.sync()}}
        private fun patchWav(file:File):Boolean =
            RandomAccessFile(file,"rw").use{f->
                val header=handsFreeWav(ShortArray(0),0)
                val previous=ByteArray(44)
                val available=f.read(previous)
                val damaged=available<44 || header.indices.any{it !in 4..7 && it !in 40..43 && previous[it]!=header[it]}
                val count=((f.length()-44).coerceAtLeast(0)/2)*2;f.setLength(44+count)
                val sizes=java.nio.ByteBuffer.wrap(header).order(java.nio.ByteOrder.LITTLE_ENDIAN)
                sizes.putInt(4,(36+count).toInt());sizes.putInt(40,count.toInt())
                f.seek(0);f.write(header);f.fd.sync()
                damaged
            }
        private fun audioInfo(file:File,path:String)=JSONObject().put("path",path).put("sampleRate",16000).put("channels",1).put("encoding","PCM16LE")
            .put("samples",((file.length()-44)/2).coerceAtLeast(0)).put("durationMs",((file.length()-44).coerceAtLeast(0)*1000/32000))
            .put("sha256",file.inputStream().use{input->val digest=MessageDigest.getInstance("SHA-256");val buffer=ByteArray(8192);while(true){val n=input.read(buffer);if(n<0)break;digest.update(buffer,0,n)};digest.digest().joinToString(""){"%02x".format(it)}})
    }
}
