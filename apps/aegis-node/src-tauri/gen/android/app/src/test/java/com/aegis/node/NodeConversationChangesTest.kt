package com.aegis.node

import org.junit.Assert.*
import org.junit.Test
import java.util.UUID

class NodeConversationChangesTest {
  @Test fun replayIsReadOnlyAndKeepsOnlyLatestRevisionPerConversation() {
    val journal=ConversationChangeJournal()
    val a=UUID.randomUUID().toString();val b=UUID.randomUUID().toString()
    val first=journal.changed("identity",a)
    journal.changed("identity",b);val latest=journal.changed("identity",a)
    val snapshot=journal.snapshot("identity")
    assertEquals(first.getString("generation"),snapshot.getString("generation"))
    assertEquals(2,snapshot.getJSONArray("changes").length())
    assertEquals(3L,latest.getLong("revision"))
    assertEquals(snapshot.toString(),journal.snapshot("identity").toString())
  }
  @Test fun identityRevocationReplacementAndProcessRestartResetTheJournal() {
    val journal=ConversationChangeJournal()
    val event=journal.changed("old",UUID.randomUUID().toString())
    val replacement=journal.snapshot("new")
    assertEquals(0,replacement.getJSONArray("changes").length())
    assertNotEquals(event.getString("generation"),replacement.getString("generation"))
    journal.changed("new",UUID.randomUUID().toString())
    assertEquals(0,journal.snapshot(null).getJSONArray("changes").length())
    assertEquals(1,journal.snapshot("new").getJSONArray("changes").length())
    assertNotEquals(replacement.getString("generation"),ConversationChangeJournal().snapshot("new").getString("generation"))
  }
  @Test fun boundedJournalEvictsOnlyOldestDistinctDestinationAndRejectsInvalidIds() {
    val journal=ConversationChangeJournal(2)
    val a=UUID.randomUUID().toString();val b=UUID.randomUUID().toString();val c=UUID.randomUUID().toString()
    journal.changed("owner",a);journal.changed("owner",b);journal.changed("owner",a);journal.changed("owner",c)
    val ids=journal.snapshot("owner").getJSONArray("changes")
    assertEquals(2,ids.length());assertEquals(a,ids.getJSONObject(0).getString("conversationId"));assertEquals(c,ids.getJSONObject(1).getString("conversationId"))
    assertThrows(IllegalArgumentException::class.java){journal.changed("owner","invalid")}
  }
}
