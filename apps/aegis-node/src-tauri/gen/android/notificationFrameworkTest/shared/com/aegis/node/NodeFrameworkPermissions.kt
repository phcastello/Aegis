package com.aegis.node

import android.content.Context
import android.content.pm.PackageManager
import android.os.Build
import android.os.ParcelFileDescriptor
import androidx.test.platform.app.InstrumentationRegistry

/** UiAutomation's permission method was added in API 28; API 26/27 use shell IPC. */
internal fun grantFrameworkPermission(context:Context,permission:String) {
 val automation=InstrumentationRegistry.getInstrumentation().uiAutomation
 if(Build.VERSION.SDK_INT>=28)automation.grantRuntimePermission(context.packageName,permission)
 else {
  require(context.packageName.matches(Regex("[A-Za-z0-9_.]+")))
  require(permission.matches(Regex("[A-Za-z0-9_.]+")))
  automation.executeShellCommand("pm grant ${context.packageName} $permission").use{descriptor->
   ParcelFileDescriptor.AutoCloseInputStream(descriptor).bufferedReader().use{it.readText()}
  }
 }
 check(context.checkSelfPermission(permission)==PackageManager.PERMISSION_GRANTED){"framework_permission_grant_failed"}
}
