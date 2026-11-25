package com.aegis.node

import java.io.ByteArrayInputStream
import java.io.File
import org.junit.Assert.*
import org.junit.Test
import kotlin.math.round

class NodeHandsFreeDoubleTalkDetectorTest {
    private val softwareScores = floatArrayOf(
        0.311676770f, 0.229986995f, 0.215583429f, 0.216318190f, 0.216083378f, 0.278700083f, 0.353765041f, 0.269746721f,
        0.252439380f, 0.273290098f, 0.254350096f, 0.241965920f, 0.388358951f, 0.376953691f, 0.360559344f, 0.403409779f,
        0.386597723f, 0.364803016f, 0.345537037f, 0.352576047f, 0.342951983f, 0.350186884f, 0.301341474f, 0.261025429f,
        0.245221049f, 0.248288512f, 0.236523271f, 0.227050632f, 0.229943812f, 0.229364544f, 0.221368805f, 0.222715452f,
        0.238753825f, 0.223662764f, 0.222942933f, 0.249841675f, 0.225856721f, 0.223264217f, 0.237788543f, 0.227782741f,
        0.222350776f, 0.234505549f, 0.232171431f, 0.223180771f, 0.227057531f, 0.248813882f, 0.227614194f, 0.225942850f,
        0.259460211f, 0.229993805f, 0.226765230f, 0.268101126f, 0.231950387f, 0.223680899f, 0.252694905f, 0.233566344f,
        0.222317353f, 0.228361368f, 0.248423919f, 0.224736571f, 0.224254087f, 0.265091896f, 0.231507644f, 0.225925878f,
        0.265516818f, 0.234958634f, 0.223442093f, 0.250752032f, 0.230569795f, 0.221949354f, 0.228703797f, 0.243259549f,
        0.222921774f, 0.224183947f, 0.246140152f, 0.225046873f, 0.227189645f, 0.269421339f, 0.234390110f, 0.226282954f,
        0.251702279f, 0.226817027f, 0.223050788f, 0.243205011f, 0.226011977f, 0.218670458f, 0.229368806f, 0.242016375f,
        0.223579600f, 0.227173775f, 0.575456381f, 0.615095258f, 0.525236249f, 0.514302731f, 0.535689831f, 0.556249440f,
        0.570985734f, 0.536641359f, 0.520101488f, 0.536654711f, 0.518678725f, 0.514979720f, 0.533288419f, 0.553870916f,
        0.539095998f, 0.528652966f, 0.544739842f, 0.520017445f, 0.528159857f, 0.527687252f, 0.503963888f, 0.428465903f,
        0.382790595f, 0.365996212f, 0.332431436f, 0.311101764f, 0.313124865f, 0.287512928f, 0.271252483f, 0.281534106f,
        0.267506152f, 0.254024982f, 0.269763947f, 0.259507984f, 0.243085101f, 0.244490728f, 0.254040897f, 0.234071985f,
        0.229669780f, 0.253973633f, 0.230406612f, 0.222920090f, 0.245720103f, 0.226049095f, 0.219474345f, 0.234740451f,
        0.226328060f, 0.216640905f, 0.222097814f, 0.230359107f, 0.214189097f, 0.213280812f, 0.238977298f, 0.216289222f,
        0.210981473f, 0.234873012f, 0.215473473f, 0.208634809f, 0.230380714f, 0.215203494f, 0.209269658f, 0.218086228f,
        0.222554743f, 0.208770022f, 0.211179525f, 0.221373200f, 0.208717629f, 0.208756924f, 0.234896004f, 0.212494582f,
        0.207941875f, 0.231008708f, 0.211196795f, 0.207002029f, 0.217995524f, 0.219955698f, 0.207228869f, 0.211075470f,
        0.219794855f, 0.206719667f, 0.207543507f, 0.233413205f, 0.211324781f, 0.206359625f, 0.233657166f, 0.212682307f,
        0.207227245f, 0.230424508f, 0.214247435f, 0.207093626f, 0.220348150f, 0.219310224f, 0.207397744f, 0.208113089f,
        0.231653482f, 0.211944789f, 0.205953747f, 0.228656039f, 0.210124612f, 0.205721587f, 0.419992387f, 0.396451205f,
        0.347840548f, 0.366306931f, 0.363410383f, 0.359791696f, 0.359552830f, 0.366644204f, 0.340268582f, 0.347191364f,
        0.373732805f, 0.349584728f, 0.301094472f, 0.285865754f, 0.263924778f, 0.246938109f, 0.249037296f, 0.238879830f,
        0.225780904f, 0.230086282f, 0.226812109f, 0.214888528f, 0.214159071f, 0.227379993f, 0.211971685f, 0.207476258f,
        0.231927857f, 0.211467162f, 0.205306321f, 0.218439847f, 0.209521651f, 0.202369243f, 0.212118074f, 0.209045380f,
        0.199672416f, 0.203826249f, 0.220297962f, 0.202451289f, 0.199997231f, 0.231542170f, 0.206221253f, 0.200616300f,
        0.225722536f, 0.205911636f, 0.200245038f, 0.217359111f, 0.206616044f, 0.199343637f, 0.204077661f, 0.218898132f,
        0.201289237f,
    )
    private val hardwareScores = floatArrayOf(
        0.313129008f, 0.231012926f, 0.216705203f, 0.217417359f, 0.217381194f, 0.331069589f, 0.369554311f, 0.288444072f,
        0.265645802f, 0.282966942f, 0.267410159f, 0.252005130f, 0.302225441f, 0.316388071f, 0.314340651f, 0.364225954f,
        0.344240308f, 0.318740338f, 0.302125484f, 0.306587577f, 0.292546421f, 0.304974228f, 0.273056239f, 0.240579292f,
        0.229794398f, 0.236947641f, 0.227581039f, 0.220836341f, 0.224823639f, 0.226970896f, 0.220431805f, 0.224287495f,
        0.240172103f, 0.227821693f, 0.226822078f, 0.251752496f, 0.230032653f, 0.228111312f, 0.240934700f, 0.231275335f,
        0.225972816f, 0.236663029f, 0.236139417f, 0.227763221f, 0.233191520f, 0.251587510f, 0.233703434f, 0.231938958f,
        0.261480570f, 0.235560417f, 0.232862249f, 0.268711776f, 0.237809271f, 0.228059083f, 0.254899293f, 0.236335665f,
        0.225549951f, 0.232571125f, 0.248516530f, 0.227670431f, 0.228722826f, 0.262438297f, 0.235008270f, 0.230587274f,
        0.262980044f, 0.239837527f, 0.226877093f, 0.250761896f, 0.232205406f, 0.224677414f, 0.232017457f, 0.244256482f,
        0.225606814f, 0.227364600f, 0.247055054f, 0.229266495f, 0.232855812f, 0.266604811f, 0.239826009f, 0.230081558f,
        0.251284629f, 0.228462085f, 0.225662544f, 0.242734835f, 0.228329688f, 0.221652970f, 0.232779086f, 0.243733153f,
        0.227629215f, 0.232782200f, 0.465908974f, 0.478952497f, 0.405640572f, 0.387791902f, 0.391772509f, 0.408269763f,
        0.423084676f, 0.392038405f, 0.372189611f, 0.387492001f, 0.360499918f, 0.348035812f, 0.365323752f, 0.387941748f,
        0.361673504f, 0.358095527f, 0.362105906f, 0.333622724f, 0.343374848f, 0.346267521f, 0.321926504f, 0.273018956f,
        0.257479221f, 0.249435991f, 0.234060481f, 0.228507116f, 0.235054284f, 0.222728938f, 0.216801852f, 0.229237899f,
        0.220379502f, 0.213800058f, 0.229805008f, 0.222130612f, 0.211119086f, 0.214984208f, 0.226959169f, 0.209731981f,
        0.209626123f, 0.234279975f, 0.212237671f, 0.207311735f, 0.232190728f, 0.213361159f, 0.208184734f, 0.225154281f,
        0.217917159f, 0.208434165f, 0.214186653f, 0.224414244f, 0.207037747f, 0.209218875f, 0.235841587f, 0.212582126f,
        0.207785502f, 0.233888283f, 0.214123487f, 0.206642613f, 0.229166389f, 0.213823080f, 0.207858980f, 0.216186076f,
        0.222064912f, 0.207216740f, 0.208754435f, 0.221346051f, 0.208754644f, 0.211358428f, 0.237642542f, 0.214758456f,
        0.210342839f, 0.233544573f, 0.212939665f, 0.209075138f, 0.219791338f, 0.221308529f, 0.208111987f, 0.210732952f,
        0.221646786f, 0.208362222f, 0.211336911f, 0.237142906f, 0.214953855f, 0.209464476f, 0.236633539f, 0.215858355f,
        0.210116103f, 0.233641356f, 0.217861652f, 0.209896848f, 0.222616687f, 0.219938681f, 0.208221465f, 0.210910425f,
        0.233812004f, 0.215207383f, 0.208557338f, 0.231541350f, 0.213659272f, 0.208898455f, 0.301396668f, 0.309917837f,
        0.273888052f, 0.295487612f, 0.285658360f, 0.289045125f, 0.299041897f, 0.300960690f, 0.268436104f, 0.277911693f,
        0.302396566f, 0.278913081f, 0.239849567f, 0.236294180f, 0.221058369f, 0.210340038f, 0.217000455f, 0.208693519f,
        0.201106593f, 0.207463428f, 0.207107425f, 0.198535979f, 0.201085404f, 0.216539532f, 0.202083647f, 0.198925734f,
        0.225129351f, 0.204375997f, 0.199272528f, 0.214362144f, 0.203874320f, 0.197741404f, 0.208145052f, 0.205813423f,
        0.196971208f, 0.203729212f, 0.221657947f, 0.202575505f, 0.201021388f, 0.230900988f, 0.205927372f, 0.200786769f,
        0.227808028f, 0.207668766f, 0.201343387f, 0.219606802f, 0.206987113f, 0.200006127f, 0.206063241f, 0.221855164f,
        0.203118995f,
    )

    private fun assetFile(): File {
        val relative = "src/main/assets/" + HandsFreeDoubleTalkDetector.ASSET
        return listOf(File(relative), File("app/$relative"),
            File("apps/aegis-node/src-tauri/gen/android/app/$relative"))
            .firstOrNull { it.isFile }
            ?: error("Packaged double-talk model not found from " + System.getProperty("user.dir"))
    }

    private fun model() = assetFile().inputStream().use { HandsFreeDoubleTalkDetector.Model.load(it) }

    private data class Frame(val mic: ShortArray,val render: ShortArray,val clean: ShortArray,val linear: ShortArray)
    private fun frame(t: Int): Frame {
        fun render(block: Int,i: Int): Short {
            val index=block*320L+i
            return ((index*7919+block*104729L)%12007-6003).toShort()
        }
        fun delayed(blocks:Int,i:Int): Double=if(t>=blocks)render(t-blocks,i).toDouble() else 0.0
        val playback=ShortArray(320){render(t,it)}
        val echo=DoubleArray(320){i->
            if(t<120)delayed(5,i)*.55+delayed(13,i)*.16 else delayed(31,i)*.70+delayed(2,i)*.12
        }
        val voice=IntArray(320){i->
            if(t in 12..21||t in 90..110||t in 190..201)(((t*320L+i)*631+173)%5077-2538).toInt() else 0
        }
        val mic=ShortArray(320){round(echo[it]+voice[it]).toInt().coerceIn(-32768,32767).toShort()}
        val clean=ShortArray(320){round(voice[it]+echo[it]*.12).toInt().toShort()}
        val linear=ShortArray(320){round(voice[it]+echo[it]*.35).toInt().toShort()}
        return Frame(mic,playback,clean,linear)
    }

    @Test fun matchesCausalPythonReferenceAcrossHistoryWrapsAndModeReset() {
        val model=model(); val detector=HandsFreeDoubleTalkDetector(model)
        assertEquals(6,detector.requiredVotes)
        assertEquals(12,detector.windowFrames)
        assertEquals(0.763680538f,model.threshold,0.000002f)
        assertEquals(0.769403059f,model.hardwareThreshold,0.000002f)
        // Both sequences exceed 1.2 s reference capacity more than three times.
        // Switching to native capture must discard all previous software state.
        for (software in listOf(true,false)) {
            val expected=if(software)softwareScores else hardwareScores
            for(t in expected.indices) {
                val f=frame(t)
                val mic=f.mic.copyOf();val render=f.render.copyOf()
                val yes=if(software)detector.observe(f.mic,f.render,f.clean,f.linear)
                    else detector.observe(f.mic,f.render)
                assertEquals("ready at frame $t",t>=2,detector.ready)
                assertEquals("score at frame $t",expected[t],detector.lastScore,.003f)
                val threshold=if(software)model.threshold else model.hardwareThreshold
                assertEquals(detector.ready&&detector.lastScore>=threshold,yes)
                assertArrayEquals(mic,f.mic);assertArrayEquals(render,f.render)
            }
        }
        detector.reset()
        assertFalse(detector.ready);assertEquals(0f,detector.lastScore,0f)
        val f=frame(0);assertFalse(detector.observe(f.mic,f.render,f.clean,f.linear))
        assertEquals(softwareScores[0],detector.lastScore,.003f)
    }

    @Test fun invalidWeightsAreRejectedBeforeCapture() {
        val data=assetFile().readBytes()
        val bad=data.copyOf();bad[0]=(bad[0].toInt() xor 1).toByte()
        assertThrows(IllegalArgumentException::class.java) {
            ByteArrayInputStream(bad).use { HandsFreeDoubleTalkDetector.Model.load(it) }
        }
        val trailing=data+byteArrayOf(1)
        assertThrows(IllegalArgumentException::class.java) {
            ByteArrayInputStream(trailing).use { HandsFreeDoubleTalkDetector.Model.load(it) }
        }
    }
}
