package com.aegis.node;

import static org.junit.Assert.*;

import android.app.Dialog;
import android.content.Context;
import android.view.Window;
import android.view.WindowManager;
import java.lang.reflect.Field;
import java.lang.reflect.Method;
import org.junit.After;
import org.junit.Before;
import org.junit.Test;
import org.junit.runner.RunWith;
import org.robolectric.RobolectricTestRunner;
import org.robolectric.RuntimeEnvironment;
import org.robolectric.Shadows;
import org.robolectric.annotation.Config;
import org.robolectric.annotation.LooperMode;
import org.robolectric.shadows.ShadowVoiceInteractionSession;

/** Executes the production assistant Session and actual Android Window flag logic.
 * Service attachment is a Robolectric boundary; no Node runtime/microphone is opened.
 * These assertions intentionally fail against immutable unstable.14 and pass only
 * once the production Session retains the screen during its active presentation.
 */
@RunWith(RobolectricTestRunner.class)
@Config(manifest = Config.NONE, sdk = 35)
@LooperMode(LooperMode.Mode.PAUSED)
public class NodeVoiceScreenRuntimeTest {
    private Object session;
    private Window window;

    private static void setField(Object value, String name, Object member) throws Exception {
        Field field = value.getClass().getDeclaredField(name);
        field.setAccessible(true);
        field.set(value, member);
    }

    private static Object getField(Object value, String name) throws Exception {
        Field field = value.getClass().getDeclaredField(name);
        field.setAccessible(true);
        return field.get(value);
    }

    private static Object call(Object value, String prefix, Object... args) throws Exception {
        for (Method method : value.getClass().getDeclaredMethods()) {
            if ((method.getName().equals(prefix) || method.getName().startsWith(prefix + "$"))
                    && method.getParameterCount() == args.length) {
                method.setAccessible(true);
                return method.invoke(value, args);
            }
        }
        throw new AssertionError("Production method absent: " + prefix);
    }

    @Before public void createProductionSession() throws Exception {
        Class<?> type = Class.forName("com.aegis.node.NodeVoiceInteractionSession");
        session = type.getConstructor(Context.class).newInstance(RuntimeEnvironment.getApplication());
        ShadowVoiceInteractionSession shadow = Shadows.shadowOf((android.service.voice.VoiceInteractionSession) session);
        shadow.create();
        Dialog dialog = ((android.service.voice.VoiceInteractionSession) session).getWindow();
        assertNotNull(dialog);
        window = dialog.getWindow();
        assertNotNull(window);
        setField(session, "visible", true);
        setField(session, "boundSession", "screen-policy-session");
        call(getField(session, "presentation"), "begin", "screen-policy-session");
        setField(session, "current", session);
    }

    @After public void cleanup() throws Exception {
        if (session != null) call(session, "onHide");
    }

    private void state(String state) throws Exception { call(session, "refresh", state, ""); }
    private boolean keepScreenOn() {
        return (window.getAttributes().flags & WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON) != 0;
    }

    @Test public void visibleInputProcessingAndPlaybackKeepTheScreenOn() throws Exception {
        for (String status : new String[] {"capturing", "continuation", "processing", "speaking"}) {
            state(status);
            assertTrue("Active production state must retain display: " + status, keepScreenOn());
        }
    }

    @Test public void idleAndFailureReleaseTheScreen() throws Exception {
        state("capturing");
        assertTrue(keepScreenOn());
        state("listening");
        assertFalse("Idle must not retain the display", keepScreenOn());
        state("capturing");
        assertTrue(keepScreenOn());
        state("session_failed");
        assertFalse("Error must not retain the display", keepScreenOn());
    }

    @Test public void absentSessionOrHiddenPresentationDoesNotHoldDisplay() throws Exception {
        setField(session, "boundSession", null);
        state("capturing");
        assertFalse("No bound turn must not retain the display", keepScreenOn());
        setField(session, "boundSession", "screen-policy-session");
        setField(session, "visible", false);
        state("capturing");
        assertFalse("Hidden presentation must not retain the display", keepScreenOn());
    }

    @Test public void hideAndDestroyClearScreenAndOneShotWakeFlags() throws Exception {
        state("capturing");
        assertTrue(keepScreenOn());
        window.addFlags(WindowManager.LayoutParams.FLAG_TURN_SCREEN_ON);
        call(session, "onHide");
        assertFalse(keepScreenOn());
        assertEquals(0, window.getAttributes().flags & WindowManager.LayoutParams.FLAG_TURN_SCREEN_ON);
        setField(session, "visible", true);
        state("capturing");
        assertTrue(keepScreenOn());
        call(session, "onDestroy");
        assertFalse(keepScreenOn());
    }

    @Test public void suppressingOneShotWakeDoesNotCancelAnActiveDisplayHold() throws Exception {
        state("capturing");
        assertTrue(keepScreenOn());
        window.addFlags(WindowManager.LayoutParams.FLAG_TURN_SCREEN_ON);
        call(session, "suppressScreenOn");
        assertEquals(0, window.getAttributes().flags & WindowManager.LayoutParams.FLAG_TURN_SCREEN_ON);
        assertTrue("Suppressing a wake request must not make an ongoing visible turn time out", keepScreenOn());
    }

    @Test public void terminalErrorImmediatelyReleasesScreenBeforeDismissTimer() throws Exception {
        state("capturing");
        assertTrue(keepScreenOn());
        call(session, "finishIfSession", "screen-policy-session", false, true);
        assertFalse("Terminal error must release display before delayed dismissal", keepScreenOn());
    }

    @Test public void explicitCancellationReleasesDisplayImmediately() throws Exception {
        state("capturing");
        assertTrue(keepScreenOn());
        call(session, "finishQuietly");
        assertFalse("Cancellation must release display without waiting for lifecycle callbacks", keepScreenOn());
    }

    @Test public void staleCancellationCannotCloseAReopenedPresentation() throws Exception {
        state("capturing");
        Runnable stale = (Runnable) call(session, "cancellationSnapshot");
        assertNotNull(stale);
        call(getField(session, "presentation"), "begin", "reopened-session");
        setField(session, "boundSession", "reopened-session");
        state("capturing");
        assertTrue(keepScreenOn());
        stale.run();
        assertTrue("A delayed callback from the previous turn must not cancel the new display hold", keepScreenOn());
        Runnable active = (Runnable) call(session, "cancellationSnapshot");
        assertNotNull(active);
        active.run();
        assertFalse("Cancellation for the current turn must release display", keepScreenOn());
    }
}
