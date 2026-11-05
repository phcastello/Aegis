package com.aegis.node;

import android.app.Activity;
import android.os.Looper;
import android.os.PowerManager;
import android.os.SystemClock;
import android.provider.Settings;
import android.view.View;
import java.lang.reflect.Field;
import java.lang.reflect.Method;
import java.time.Duration;
import java.util.Arrays;
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
import org.robolectric.shadows.ShadowLooper;
import org.robolectric.shadows.ShadowChoreographer;
import org.robolectric.shadows.ShadowView;
import static org.junit.Assert.*;

@RunWith(RobolectricTestRunner.class)
@Config(manifest=Config.NONE, sdk=35)
@LooperMode(LooperMode.Mode.PAUSED)
@GraphicsMode(GraphicsMode.Mode.NATIVE)
public class HandsFreePanelViewRuntimeTest {
 int drawCount;
 Activity activity;
 HandsFreePanelView view;
 Object owner;
 @Before public void setup() throws Exception {
  ShadowView.setUseRealDrawTraversals(true);
  ShadowChoreographer.setPaused(true);
  ShadowChoreographer.setFrameDelay(Duration.ofMillis(16));
  activity=Robolectric.buildActivity(Activity.class).setup().visible().get();
  Settings.Global.putFloat(activity.getContentResolver(), Settings.Global.ANIMATOR_DURATION_SCALE, 1f);
  Shadows.shadowOf(activity.getSystemService(PowerManager.class)).setIsPowerSaveMode(false);
  view=new HandsFreePanelView(activity);
  activity.setContentView(view);
  view.layout(0,0,360,72);
  view.getViewTreeObserver().addOnDrawListener(()->drawCount++);
  owner=new Object();
  call("show",owner,"session");
  call("state",HandsFreePanelState.CAPTURING);
  ShadowLooper.idleMainLooper(32,java.util.concurrent.TimeUnit.MILLISECONDS);
  System.out.println("realView attached="+view.isAttachedToWindow()+" shown="+view.isShown()+" window="+view.getWindowVisibility()+" signalBound="+field("signalBound")+" reduceMotion="+field("reduceMotion"));
  assertTrue(view.isAttachedToWindow());assertTrue(view.isShown());
 }
 @After public void teardown() throws Exception {if(view!=null)call("hide");}
 void call(String prefix,Object...args) throws Exception {
  for(Method m:HandsFreePanelView.class.getDeclaredMethods())if(m.getName().startsWith(prefix+"$") && m.getParameterCount()==args.length){m.setAccessible(true);m.invoke(view,args);return;}
  throw new AssertionError("Missing production method "+prefix);
 }
 Object field(String name) throws Exception {Field f=HandsFreePanelView.class.getDeclaredField(name);f.setAccessible(true);return f.get(view);}
 float max(float[] a) {float r=0;for(float v:a)r=Math.max(r,v);return r;}
 void audio() {
  short[] pcm=new short[3200];for(int i=0;i<pcm.length;i++)pcm[i]=(short)((i%2==0?1:-1)*(100+((i/320)*173)%2200));
  HandsFreePanelSignal.INSTANCE.capture("session",pcm,pcm.length,SystemClock.elapsedRealtime());
 }
 @Test public void realViewRunsIntermediateVsyncFrames() throws Exception {
  ShadowLooper.idleMainLooper(250,java.util.concurrent.TimeUnit.MILLISECONDS);audio();ShadowLooper.idleMainLooper();
  assertEquals(false,field("reduceMotion"));
  System.out.println("start draws="+drawCount+" framePending="+field("framePending")+" dirty="+view.isDirty());
  int changed=0,frames=0;int before=drawCount;float[] previous=((float[])field("amplitudes")).clone();
  for(int i=0;i<11;i++){
   ShadowLooper.idleMainLooper(17,java.util.concurrent.TimeUnit.MILLISECONDS);
   // Draw is performed ONLY by the real ViewRoot traversal, never by this fixture.
   float[] current=(float[])field("amplitudes");if(!Arrays.equals(previous,current))changed++;
   previous=current.clone();frames++;
   System.out.println("tick="+i+" elapsed="+SystemClock.elapsedRealtime()+" draws="+drawCount+" pending="+field("framePending")+" dirty="+view.isDirty()+" max="+max(current));
  }
  System.out.println("real View actual draws="+(drawCount-before)+" frames="+frames+" changed="+changed+" framePending="+field("framePending"));
  assertTrue("Real ViewRoot must draw between input batches without forced draw",drawCount-before>=8);
  assertTrue("Production onDraw must advance measured history between input batches",changed>=8);
 }
 @Test public void batterySaverWithAnimationsEnabledDoesNotFreezeInputPresentation() throws Exception {
  Shadows.shadowOf(activity.getSystemService(PowerManager.class)).setIsPowerSaveMode(true);
  Method update=HandsFreePanelView.class.getDeclaredMethod("updateMotion");update.setAccessible(true);update.invoke(view);
  ShadowLooper.idleMainLooper(250,java.util.concurrent.TimeUnit.MILLISECONDS);audio();ShadowLooper.idleMainLooper();
  System.out.println("power saver real View reduceMotion="+field("reduceMotion")+" framePending="+field("framePending"));
  int before=drawCount;float[] previous=((float[])field("amplitudes")).clone();int changed=0;
  for(int i=0;i<11;i++){ShadowLooper.idleMainLooper(17,java.util.concurrent.TimeUnit.MILLISECONDS);float[] current=(float[])field("amplitudes");if(!Arrays.equals(previous,current))changed++;previous=current.clone();}
  System.out.println("power saver actual draws="+(drawCount-before)+" changed="+changed);
  assertEquals("Battery saving is not an explicit remove-motion preference",false,field("reduceMotion"));
  assertTrue("Power saver must keep real ViewRoot frames between 200ms audio batches",drawCount-before>=8);
  assertTrue("Power saver must preserve presentation of measured audio history",changed>=8);
 }
}
