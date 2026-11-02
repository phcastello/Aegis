package com.aegis.node

import okhttp3.OkHttpClient
import okhttp3.Response
import okhttp3.WebSocket
import okhttp3.WebSocketListener
import okhttp3.mockwebserver.MockResponse
import okhttp3.mockwebserver.MockWebServer
import okio.ByteString
import org.json.JSONObject
import org.junit.Assert.*
import org.junit.Test
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit
import java.util.concurrent.CopyOnWriteArrayList
import java.util.concurrent.atomic.AtomicBoolean
import java.util.concurrent.atomic.AtomicReference

class NodeHandsFreeRealtimeTest {
    private val client=OkHttpClient.Builder().retryOnConnectionFailure(false).build()
    private fun identity(server:MockWebServer)=NodeNativeSession.Session(server.url("/").toString().trimEnd('/'),"node","test-secret")
    private fun ready(id:String)=JSONObject().put("type","ready").put("transcriptionRequestId",id).put("minAudioMilliseconds",0).toString()
    private fun final(id:String)=JSONObject().put("type","final").put("transcriptionRequestId",id).put("text","não, 27, às 13h40, Ana").toString()

    @Test fun streamsSuppliedPostActivationPcmInOrderWithoutSendingCommitBeforeLocalEndpoint() {
        MockWebServer().use { server ->
            val frames=CopyOnWriteArrayList<ByteString>();val commands=CopyOnWriteArrayList<String>()
            val ready=CountDownLatch(1);val received=CountDownLatch(16)
            val peer=AtomicReference<WebSocket>();val requestId=AtomicReference<String>()
            server.enqueue(MockResponse().withWebSocketUpgrade(object:WebSocketListener(){
                override fun onOpen(ws:WebSocket,response:Response){peer.set(ws)}
                override fun onMessage(ws:WebSocket,bytes:ByteString){frames.add(bytes);received.countDown()}
                override fun onMessage(ws:WebSocket,text:String){commands.add(text);ws.send(final(requestId.get()))}
            }))
            val attempt=HandsFreeRealtimeTranscription(client,identity(server),{})
            val request=server.takeRequest(5,TimeUnit.SECONDS)!!;requestId.set(attempt.requestId)
            assertEquals("AegisNode test-secret",request.getHeader("Authorization"))
            assertEquals(attempt.requestId,request.requestUrl!!.queryParameter("transcriptionRequestId"))
            val initialPost=ShortArray(40000){it.toShort()};val fresh=ShortArray(8000){(-it).toShort()}
            attempt.append(initialPost,initialPost.size);attempt.append(fresh,fresh.size)
            while(peer.get()==null)Thread.yield()
            peer.get().send(ready(attempt.requestId));ready.countDown()
            assertTrue(received.await(5,TimeUnit.SECONDS));assertTrue(commands.isEmpty())
            assertTrue(frames.all{it.size<=6400});assertEquals(96000,frames.sumOf{it.size})
            val delivered=frames.flatMap{it.toByteArray().toList()}.toByteArray()
            val expected=handsFreeWav(initialPost+fresh,48000).copyOfRange(44,96044)
            assertArrayEquals(expected,delivered)
            attempt.commit();assertEquals("não, 27, às 13h40, Ana",attempt.awaitFinal())
            assertEquals(listOf("{\"type\":\"commit\"}"),commands);attempt.close()
        }
    }

    @Test fun shortActualAudioCommitsImmediatelyWithoutPaddingOrAnotherEndpointWait() {
        MockWebServer().use { server ->
            val commits=CopyOnWriteArrayList<String>();val received=CountDownLatch(1)
            val id=java.util.UUID.randomUUID().toString();var audioBytes=0
            server.enqueue(MockResponse().withWebSocketUpgrade(object:WebSocketListener(){
                override fun onOpen(ws:WebSocket,response:Response){ws.send(ready(id))}
                override fun onMessage(ws:WebSocket,bytes:ByteString){audioBytes+=bytes.size}
                override fun onMessage(ws:WebSocket,text:String){commits.add(text);ws.send(final(id));received.countDown()}
            }))
            val attempt=HandsFreeRealtimeTranscription(client,identity(server),{},requestId=id)
            attempt.append(ShortArray(8000),8000);attempt.commit()
            assertTrue(received.await(5,TimeUnit.SECONDS));assertEquals("não, 27, às 13h40, Ana",attempt.awaitFinal())
            assertEquals(16000,audioBytes);assertEquals(1,commits.size);attempt.close()
        }
    }

    @Test fun partialsOrStaleFinalCannotAuthorizeACommandAndTimeoutIsBounded() {
        for (type in listOf("partial","final")) MockWebServer().use { server ->
            val peer=AtomicReference<WebSocket>()
            server.enqueue(MockResponse().withWebSocketUpgrade(object:WebSocketListener(){override fun onOpen(ws:WebSocket,response:Response){peer.set(ws)}}))
            val attempt=HandsFreeRealtimeTranscription(client,identity(server),{},finalTimeoutMilliseconds=100)
            server.takeRequest(5,TimeUnit.SECONDS)
            while(peer.get()==null)Thread.yield()
            peer.get().send(ready(attempt.requestId))
            peer.get().send(JSONObject().put("type",type).put("transcriptionRequestId",attempt.requestId).put("text","partial must never execute").toString())
            attempt.append(ShortArray(40000),40000)
            if(type=="partial")attempt.commit()
            assertNull(attempt.awaitFinal());attempt.close()
        }
    }

    @Test fun failedUpgradeAndCancellationSelectOnlyCapturedBatchAndIdentityLossNeverDoes() {
        MockWebServer().use { server ->
            server.enqueue(MockResponse().setResponseCode(503))
            val current=AtomicBoolean(true)
            val attempt=HandsFreeRealtimeTranscription(client,identity(server),{check(current.get()){ "identity_changed" }},finalTimeoutMilliseconds=200)
            attempt.append(ShortArray(40000),40000);attempt.commit();assertNull(attempt.awaitFinal())
            current.set(false);assertThrows(IllegalStateException::class.java){attempt.awaitFinal()};attempt.close()
        }
        MockWebServer().use {server->
            server.enqueue(MockResponse().withWebSocketUpgrade(object:WebSocketListener(){}))
            val attempt=HandsFreeRealtimeTranscription(client,identity(server),{},finalTimeoutMilliseconds=100)
            attempt.close();attempt.append(ShortArray(40000),40000);attempt.commit();assertNull(attempt.awaitFinal())
        }
    }

    @Test fun slowConnectionQueueHasAFixedCeiling() {
        MockWebServer().use {server->
            server.enqueue(MockResponse().withWebSocketUpgrade(object:WebSocketListener(){}))
            val attempt=HandsFreeRealtimeTranscription(client,identity(server),{},finalTimeoutMilliseconds=100)
            repeat(30){attempt.append(ShortArray(3200),3200)}
            attempt.commit();assertNull(attempt.awaitFinal());attempt.close()
        }
    }

    @Test fun partialDisplayIsBoundedClearedOnFinalAndNeverBecomesACommand() {
        MockWebServer().use {server->
            val id=java.util.UUID.randomUUID().toString()
            val shown=CopyOnWriteArrayList<String>();val displayed=CountDownLatch(1)
            val peer=AtomicReference<WebSocket>()
            server.enqueue(MockResponse().withWebSocketUpgrade(object:WebSocketListener(){
                override fun onOpen(ws:WebSocket,response:Response){peer.set(ws);ws.send(ready(id))
                    ws.send(JSONObject().put("type","partial").put("transcriptionRequestId",id).put("text","p".repeat(10000)).toString())}
                override fun onMessage(ws:WebSocket,text:String){ws.send(final(id))}
            }))
            val attempt=HandsFreeRealtimeTranscription(client,identity(server),{},requestId=id,onPartial={shown.add(it);if(it.isNotEmpty())displayed.countDown()})
            assertTrue(displayed.await(5,TimeUnit.SECONDS))
            assertEquals(listOf("p".repeat(240)),shown.toList())
            attempt.append(ShortArray(3200),3200);attempt.commit()
            assertEquals("não, 27, às 13h40, Ana",attempt.awaitFinal())
            assertEquals("",shown.last())
            attempt.close()
            peer.get().send(JSONObject().put("type","partial").put("transcriptionRequestId",id).put("text","late").toString())
            assertFalse(shown.contains("late"))
        }
    }

    @Test fun staleRequestOrRevokedCaptureNeverDisplaysPartialAndPresenterFailureCannotFailFinal() {
        for(staleRequest in listOf(true,false))MockWebServer().use {server->
            val peer=AtomicReference<WebSocket>();val active=AtomicBoolean(true)
            val shown=CopyOnWriteArrayList<String>()
            server.enqueue(MockResponse().withWebSocketUpgrade(object:WebSocketListener(){override fun onOpen(ws:WebSocket,response:Response){peer.set(ws)}}))
            val attempt=HandsFreeRealtimeTranscription(client,identity(server),{},finalTimeoutMilliseconds=100,
                checkActive={check(active.get())},onPartial={shown.add(it)})
            server.takeRequest(5,TimeUnit.SECONDS)
            while(peer.get()==null)Thread.yield()
            peer.get().send(ready(attempt.requestId))
            if(!staleRequest)active.set(false)
            peer.get().send(JSONObject().put("type","partial").put("transcriptionRequestId",if(staleRequest)"another-request" else attempt.requestId).put("text","must not display").toString())
            attempt.append(ShortArray(3200),3200);attempt.commit();assertNull(attempt.awaitFinal());attempt.close()
            assertTrue(shown.all{it.isEmpty()})
        }
        MockWebServer().use {server->
            val id=java.util.UUID.randomUUID().toString()
            server.enqueue(MockResponse().withWebSocketUpgrade(object:WebSocketListener(){
                override fun onOpen(ws:WebSocket,response:Response){ws.send(ready(id));ws.send(JSONObject().put("type","partial").put("transcriptionRequestId",id).put("text","temporary").toString())}
                override fun onMessage(ws:WebSocket,text:String){ws.send(final(id))}
            }))
            val attempt=HandsFreeRealtimeTranscription(client,identity(server),{},requestId=id,onPartial={throw IllegalStateException("view unavailable")})
            attempt.append(ShortArray(3200),3200);attempt.commit();assertEquals("não, 27, às 13h40, Ana",attempt.awaitFinal());attempt.close()
        }
    }
}
