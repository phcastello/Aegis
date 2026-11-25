package com.aegis.node

import kotlin.math.abs
import org.junit.Assert.*
import org.junit.Test

class HandsFreeThinkingMotionTest {
    private fun at(ms:Long)=FloatArray(6).also{HandsFreeThinkingMotion.sample(ms,it)}

    @Test fun approvedLoopAndRestingFrame() {
        assertEquals(10334L,HandsFreeThinkingMotion.DURATION_MS)
        assertArrayEquals(floatArrayOf(-6.6f,3.744f,0f,-2.963f,6.6f,-5f),at(0L),.05f)
        for(ms in listOf(0L,135L,799L,1997L,2712L,6200L,7722L,10200L)) {
            assertArrayEquals(at(ms),at(ms+HandsFreeThinkingMotion.DURATION_MS),.0001f)
        }
    }

    @Test fun initialMovementIsVerticalAndLaterDotsChangeHorizontalPosition() {
        val start=at(0L)
        val vertical=at(450L)
        for(index in 0..2) assertEquals(start[index*2],vertical[index*2],.05f)
        assertTrue((0..2).any{abs(start[it*2+1]-vertical[it*2+1])>2f})
        val subsequent=at(4000L)
        assertTrue((0..2).any{abs(start[it*2]-subsequent[it*2])>2f})
    }

    @Test fun samplesStayInsideTheViewboxAndTheLoopIsContinuous() {
        val previous=at(0L)
        val next=FloatArray(6)
        for(ms in 16L..HandsFreeThinkingMotion.DURATION_MS step 16L) {
            HandsFreeThinkingMotion.sample(ms,next)
            // Model units are relative to the centre of the 28 x 28 viewbox.
            assertTrue(next.all{it.isFinite() && abs(it)<=12.01f})
            assertTrue("Motion jumps at $ms ms",next.indices.all{abs(next[it]-previous[it])<2f})
            next.copyInto(previous)
        }
        val last=at(HandsFreeThinkingMotion.DURATION_MS-1)
        assertArrayEquals(at(0L),last,.12f)
        // The removed vertical repeat must not introduce a visible discontinuity.
        assertArrayEquals(at(798L),at(800L),.2f)
    }
}
