package com.aegis.node;

import static org.junit.Assert.*;

import android.app.Activity;
import android.os.PowerManager;
import android.os.SystemClock;
import android.provider.Settings;
import android.view.View;
import java.lang.reflect.Field;
import java.lang.reflect.Method;
import java.time.Duration;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicBoolean;
import org.junit.After;
import org.junit.Before;
import org.junit.Test;
import org.junit.runner.RunWith;
import org.robolectric.Robolectric;
import org.robolectric.RobolectricTestRunner;
import org.robolectric.Shadows;
import org.robolectric.annotation.Config;
import org.robolectric.annotation.GraphicsMode;
import org.robolectric.annotation.LooperMode;
import org.robolectric.shadows.ShadowChoreographer;
import org.robolectric.shadows.ShadowLooper;
import org.robolectric.shadows.ShadowView;

/** Production ViewRoot/lifecycle tests: this fixture never calls draw/onDraw. */
@RunWith(RobolectricTestRunner.class)
@Config(manifest = Config.NONE, sdk = 35)
@LooperMode(LooperMode.Mode.PAUSED)
@GraphicsMode(GraphicsMode.Mode.NATIVE)
public class HandsFreePanelViewLifecycleRuntimeTest {
    Activity activity;
    HandsFreePanelView view;
    Object owner;
    int draws;

    @Before public void attach() throws Exception {
        ShadowView.setUseRealDrawTraversals(true);
        ShadowChoreographer.setPaused(true);
        ShadowChoreographer.setFrameDelay(Duration.ofMillis(16));
        activity = Robolectric.buildActivity(Activity.class).setup().visible().get();
        Settings.Global.putFloat(activity.getContentResolver(), Settings.Global.ANIMATOR_DURATION_SCALE, 1f);
        Shadows.shadowOf(activity.getSystemService(PowerManager.class)).setIsPowerSaveMode(false);
        view = new HandsFreePanelView(activity);
        activity.setContentView(view);
        view.layout(0, 0, 360, 72);
        view.getViewTreeObserver().addOnDrawListener(() -> draws++);
        owner = new Object();
        call("show", owner, "original");
        call("state", HandsFreePanelState.CAPTURING);
        idle(250);
        assertTrue(view.isAttachedToWindow());
        assertTrue(view.isShown());
    }

    @After public void detach() throws Exception {
        if (view != null) call("hide");
    }

    void idle(long ms) {
        // A paused Choreographer releases one display pulse per looper advance.
        // Pace display time, rather than jumping a complete 200ms capture batch.
        while (ms > 0) {
            long step = Math.min(ms, 17);
            ShadowLooper.idleMainLooper(step, TimeUnit.MILLISECONDS);
            ms -= step;
        }
    }
    void call(String prefix, Object... args) throws Exception {
        for (Method method : HandsFreePanelView.class.getDeclaredMethods()) {
            if ((method.getName().equals(prefix) || method.getName().startsWith(prefix + "$"))
                    && method.getParameterCount() == args.length) {
                method.setAccessible(true);
                method.invoke(view, args);
                return;
            }
        }
        throw new AssertionError("Production method absent: " + prefix);
    }
    Object field(String name) throws Exception {
        Field field = HandsFreePanelView.class.getDeclaredField(name);
        field.setAccessible(true);
        return field.get(view);
    }
    float amplitude() throws Exception {
        float result = 0f;
        for (float value : (float[]) field("amplitudes")) result = Math.max(result, value);
        return result;
    }
    void audio(String token) { audio(token, SystemClock.elapsedRealtime()); }
    void audio(String token, long timestamp) {
        short[] samples = new short[3200];
        for (int i = 0; i < samples.length; i++)
            samples[i] = (short) ((i % 2 == 0 ? 1 : -1) * (100 + (i / 320) * 190));
        HandsFreePanelSignal.INSTANCE.capture(token, samples, samples.length, timestamp);
    }

    @Test public void hiddenViewStopsCallbacksAndReattachesWithEmptyHistory() throws Exception {
        audio("original"); idle(64);
        assertTrue(amplitude() > 0f);
        view.setVisibility(View.INVISIBLE); idle(64);
        assertEquals(false, field("signalBound"));
        assertEquals(false, field("scaleObserved"));
        assertEquals(false, field("framePending"));
        assertFalse(((AtomicBoolean) field("wakePosted")).get());
        assertEquals(0f, amplitude(), 0f);
        int stopped = draws;
        audio("original"); idle(240);
        assertEquals("Hidden panel must not keep traversing for incoming audio", stopped, draws);
        view.setVisibility(View.VISIBLE); idle(32);
        assertEquals(true, field("signalBound"));
        assertEquals(true, field("scaleObserved"));
        assertEquals("Old input must not reappear after visibility returns", 0f, amplitude(), 0f);
        audio("original"); idle(64);
        assertTrue("Visible panel must resume listening to existing capture", amplitude() > 0f);
    }

    @Test public void hideAndReopenRejectsPreviousSessionAndHasOneFrameChain() throws Exception {
        audio("original"); idle(64);
        assertTrue(amplitude() > 0f);
        call("hide");
        call("show", new Object(), "reopened");
        call("state", HandsFreePanelState.CAPTURING); idle(32);
        assertEquals(0f, amplitude(), 0f);
        audio("original"); idle(96);
        assertEquals("Late input from old session must not enter the new waveform", 0f, amplitude(), 0f);
        assertEquals(false, field("framePending"));
        audio("reopened");
        int before = draws; idle(192);
        assertTrue(amplitude() > 0f);
        assertTrue("Reopen must not schedule duplicate traversal chains", draws - before <= 13);
        assertTrue("Reopen must preserve continuous actual vsync presentation", draws - before >= 8);
    }

    @Test public void stateChangeDropsPreviousInputAndLateSamples() throws Exception {
        audio("original"); idle(64);
        assertTrue(amplitude() > 0f);
        long oldCapture = SystemClock.elapsedRealtime();
        call("state", HandsFreePanelState.PROCESSING); idle(32);
        assertEquals("Processing is separate from input waveform", 0f, amplitude(), 0f);
        call("state", HandsFreePanelState.CONTINUATION); idle(32);
        audio("original", oldCapture); idle(64);
        assertEquals("Pre-transition capture must not leak into continuation", 0f, amplitude(), 0f);
        idle(200); audio("original"); idle(64);
        assertTrue("New continuation input must still animate", amplitude() > 0f);
    }

    @Test public void explicitRemoveAnimationsShowsRealDataThenClearsWithoutAFrameLoop() throws Exception {
        Settings.Global.putFloat(activity.getContentResolver(), Settings.Global.ANIMATOR_DURATION_SCALE, 0f);
        call("updateMotion"); idle(32);
        assertEquals(true, field("reduceMotion"));
        audio("original"); idle(64);
        assertTrue("Remove animations must retain measured audio feedback", amplitude() > 0f);
        assertEquals("Explicit remove animations must suppress recurring vsync", false, field("framePending"));
        idle(HandsFreePanelSignal.STALE_CLEAR_MS + 100);
        assertEquals("Stopped capture must not leave a frozen waveform", 0f, amplitude(), 0f);
        int stopped = draws; idle(320);
        assertEquals("A silent reduced-motion panel must not keep drawing", stopped, draws);
    }
}
