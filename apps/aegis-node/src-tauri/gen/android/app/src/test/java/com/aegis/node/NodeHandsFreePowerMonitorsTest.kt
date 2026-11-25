package com.aegis.node

import org.junit.Assert.*
import org.junit.Test
import java.util.ArrayDeque
import java.util.concurrent.AbstractExecutorService
import java.util.concurrent.Executor
import java.util.concurrent.TimeUnit

class NodeHandsFreePowerMonitorsTest {
    private class ManualWorker:AbstractExecutorService() {
        private val tasks=ArrayDeque<Runnable>()
        var submissions=0;private var stopped=false
        fun runNext(){checkNotNull(tasks.poll()).run()}
        override fun execute(command:Runnable){check(!stopped);submissions++;tasks.add(command)}
        override fun shutdown(){stopped=true}
        override fun shutdownNow():MutableList<Runnable>{stopped=true;return tasks.toMutableList().also{tasks.clear()}}
        override fun isShutdown()=stopped
        override fun isTerminated()=stopped && tasks.isEmpty()
        override fun awaitTermination(timeout:Long,unit:TimeUnit)=isTerminated
    }
    private class Source:HandsFreePowerMonitorSource {
        val callbacks=ArrayList<(HandsFreePowerMonitorResult)->Unit>()
        override fun request(executor:Executor,callback:(HandsFreePowerMonitorResult)->Unit){callbacks.add(callback)}
        fun reply(index:Int=callbacks.lastIndex,energy:Long=100,timestamp:Long=1000,name:String="rail") {
            callbacks[index](HandsFreePowerMonitorResult.Readings(listOf(
                HandsFreePowerMonitorReading(name,HandsFreePowerMonitorKind.MEASUREMENT,energy,timestamp))))
        }
    }
    private class Clock {var now=1000L;var wall=1234567890000L}
    private fun probe(clock:Clock,worker:ManualWorker,source:Source,sdk:Int=35)=
        HandsFreePowerMonitors(sdk,source,{clock.now},{clock.wall},worker)

    @Test fun api33IsExplicitlyUnsupportedAndNeverStartsAWorker() {
        val clock=Clock();val worker=ManualWorker();val source=Source()
        probe(clock,worker,source,33).use { monitor->
            monitor.request();monitor.request(force=true)
            val state=monitor.state()
            assertEquals("unsupported_api",state.getString("status"))
            assertFalse(state.getBoolean("supportedApi"));assertEquals(35,state.getInt("minimumApi"))
            assertTrue(state.getBoolean("stale"));assertEquals(0,state.getJSONArray("monitors").length())
            assertEquals(0,worker.submissions);assertEquals(0,source.callbacks.size)
        }
    }
    @Test fun requestIsAsynchronousAndTheOneMinuteLimitAlsoAppliesToForce() {
        val clock=Clock();val worker=ManualWorker();val source=Source()
        probe(clock,worker,source).use { monitor->
            monitor.request();assertEquals("pending",monitor.state().getString("status"))
            assertEquals(0,source.callbacks.size)
            repeat(20){monitor.request(force=true)};assertEquals(1,worker.submissions)
            worker.runNext();clock.now=1100;source.reply(timestamp=1000)
            val state=monitor.state()
            assertEquals("available",state.getString("status"));assertFalse(state.getBoolean("stale"))
            assertEquals("whole_device",state.getString("scope"));assertEquals("uWs",state.getString("energyUnit"))
            assertEquals(1100,state.getLong("observedAtElapsedMs"));assertEquals(1100,state.getLong("sampledAtElapsedMs"))
            clock.now=60999;monitor.request(force=true);assertEquals(1,worker.submissions)
            clock.now=61000;monitor.request(force=true);assertEquals(2,worker.submissions)
            assertEquals("manual",monitor.state().getString("requestTrigger"))
        }
    }
    @Test fun readingsAreBoundedTypedAndNeverExportUnavailableSentinels() {
        val clock=Clock();val worker=ManualWorker();val source=Source()
        probe(clock,worker,source).use { monitor->
            monitor.request();worker.runNext()
            val rows=List(80){i->HandsFreePowerMonitorReading("x".repeat(300),
                if(i%2==0)HandsFreePowerMonitorKind.MEASUREMENT else HandsFreePowerMonitorKind.CONSUMER,
                when(i){0->-1;1->Long.MIN_VALUE;else->0},if(i==2)Long.MAX_VALUE else 1000)}
            source.callbacks.single()(HandsFreePowerMonitorResult.Readings(rows,80))
            val state=monitor.state();val result=state.getJSONArray("monitors")
            assertEquals(64,result.length());assertEquals(80,state.getInt("totalMonitorCount"));assertTrue(state.getBoolean("truncated"))
            assertEquals(128,result.getJSONObject(0).getString("name").length)
            assertEquals("measurement",result.getJSONObject(0).getString("type"))
            assertEquals("consumer",result.getJSONObject(1).getString("type"))
            for(i in 0..2){assertFalse(result.getJSONObject(i).getBoolean("available"));assertFalse(result.getJSONObject(i).has("energyUws"))}
            assertFalse(result.getJSONObject(2).has("timestampElapsedMs"))
            assertTrue(result.getJSONObject(3).getBoolean("available"));assertEquals(0,result.getJSONObject(3).getLong("energyUws"))
            assertTrue(state.toString().length<25000)
        }
    }
    @Test fun timeoutIsObservedPassivelyAndAnOldReplyCannotReplaceNewReadings() {
        val clock=Clock();val worker=ManualWorker();val source=Source()
        probe(clock,worker,source).use { monitor->
            monitor.request();worker.runNext();clock.now=11000
            assertEquals("timeout",monitor.state().getString("status"));assertTrue(monitor.state().getBoolean("stale"))
            assertEquals(1,worker.submissions)
            clock.now=61000;monitor.request();worker.runNext();clock.now=61100
            source.reply(index=1,energy=300,timestamp=61000,name="new")
            source.reply(index=0,energy=1,timestamp=1000,name="old")
            val state=monitor.state();assertEquals("available",state.getString("status"))
            val row=state.getJSONArray("monitors").getJSONObject(0)
            assertEquals("new",row.getString("name"));assertEquals(300,row.getLong("energyUws"))
            assertEquals(61100,state.getLong("observedAtElapsedMs"))
        }
    }
    @Test fun errorRetainsTheLastSnapshotWithItsTimestampAndMarksItStale() {
        val clock=Clock();val worker=ManualWorker();val source=Source()
        probe(clock,worker,source).use { monitor->
            monitor.request();worker.runNext();source.reply()
            clock.now=61000;clock.wall+=60000;monitor.request();worker.runNext()
            source.callbacks[1](HandsFreePowerMonitorResult.Error)
            val state=monitor.state();assertEquals("error",state.getString("status"));assertTrue(state.getBoolean("stale"))
            assertEquals(1000,state.getLong("sampledAtElapsedMs"));assertEquals(61000,state.getLong("observedAtElapsedMs"))
            assertEquals(1,state.getJSONArray("monitors").length());assertEquals("readings_unavailable",state.getString("error"))
        }
    }
    @Test fun noHardwareAndStaleNativeTimestampsAreNotFreshMeasurements() {
        val clock=Clock();val worker=ManualWorker();val source=Source()
        probe(clock,worker,source).use { monitor->
            monitor.request();worker.runNext();source.callbacks.single()(HandsFreePowerMonitorResult.Readings(emptyList()))
            assertEquals("unavailable",monitor.state().getString("status"))
            assertEquals("no_power_monitors",monitor.state().getString("error"))
            clock.now=200000;monitor.request();worker.runNext();source.reply(timestamp=0)
            assertTrue(monitor.state().getBoolean("stale"))
            assertTrue(monitor.state().getJSONArray("monitors").getJSONObject(0).getBoolean("stale"))
        }
    }
    @Test fun aLateCompletionIsTimedOutEvenWithoutAnEarlierStateCheck() {
        val clock=Clock();val worker=ManualWorker();val source=Source()
        probe(clock,worker,source).use { monitor->
            monitor.request();worker.runNext();clock.now=11000;source.reply(timestamp=11000)
            assertEquals("timeout",monitor.state().getString("status"))
            assertEquals(0,monitor.state().getJSONArray("monitors").length())
        }
    }
    @Test fun closeInvalidatesCallbacksAndExecutorFailuresDoNotEscape() {
        val clock=Clock();val worker=ManualWorker();val source=Source()
        val monitor=probe(clock,worker,source)
        monitor.request();worker.runNext();monitor.close();monitor.close();source.reply()
        assertEquals("closed",monitor.state().getString("status"));assertTrue(worker.isShutdown)
        monitor.request(force=true);assertEquals(1,worker.submissions)
        val stopped=ManualWorker().apply{shutdown()}
        probe(clock,stopped,Source()).use { other->
            other.request();assertEquals("error",other.state().getString("status"));assertTrue(other.state().getBoolean("stale"))
        }
    }
}
