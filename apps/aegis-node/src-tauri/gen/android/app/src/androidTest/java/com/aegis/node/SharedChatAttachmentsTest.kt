package com.aegis.node

import android.content.ClipData
import android.content.Intent
import android.content.pm.PackageManager
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test
import org.junit.runner.RunWith

@RunWith(AndroidJUnit4::class)
class SharedChatAttachmentsTest {
    private val context = InstrumentationRegistry.getInstrumentation().targetContext

    @Test
    @Suppress("DEPRECATION")
    fun sharesheetMatchesTextDocumentsPhotosVideosAndMultipleFiles() {
        val types = listOf("text/plain", "image/png", "video/mp4", "application/pdf",
            "application/vnd.openxmlformats-officedocument.wordprocessingml.document", "application/zip")
        for (type in types) {
            val single = Intent(Intent.ACTION_SEND).setType(type).setPackage(context.packageName)
            assertTrue("Missing ACTION_SEND for $type", matchesMain(single))
        }
        val multiple = Intent(Intent.ACTION_SEND_MULTIPLE).setType("*/*").setPackage(context.packageName)
        assertTrue("Missing ACTION_SEND_MULTIPLE for mixed files", matchesMain(multiple))
        val view = Intent(Intent.ACTION_VIEW).setType("text/plain").setPackage(context.packageName)
        assertFalse("Receiving shared data must not register as a general file viewer", matchesMain(view))
    }

    @Test
    fun textAndLinkSharesReachTheDraftOnce() {
        val originalLink = "https://example.com/guia?id=7#mapa"
        assertTrue(SharedChatAttachments.accept(Intent(Intent.ACTION_SEND).setType("text/plain")
            .putExtra(Intent.EXTRA_TEXT, originalLink)))
        val clip = Intent(Intent.ACTION_SEND).setType("text/plain").apply {
            clipData = ClipData.newPlainText("Frase selecionada", "Horário da prova")
        }
        assertTrue(SharedChatAttachments.accept(clip))
        val result = SharedChatAttachments.take(context)
        assertEquals(0, result.getJSONArray("files").length())
        assertEquals(originalLink, result.getJSONArray("texts").getString(0))
        assertEquals("Horário da prova", result.getJSONArray("texts").getString(1))
        assertFalse(SharedChatAttachments.take(context).has("texts"))
    }

    @Suppress("DEPRECATION")
    private fun matchesMain(intent: Intent): Boolean =
        context.packageManager.queryIntentActivities(intent, PackageManager.MATCH_DEFAULT_ONLY)
            .any { it.activityInfo.name == MainActivity::class.java.name && it.activityInfo.exported }
}
