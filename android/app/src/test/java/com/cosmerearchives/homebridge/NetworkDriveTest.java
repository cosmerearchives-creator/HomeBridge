package com.cosmerearchives.homebridge;

import static org.junit.Assert.*;
import java.util.List;
import org.junit.Test;

public class NetworkDriveTest {
    @Test public void keepsShareAndFilenameAsSeparateComponents() {
        assertEquals("smb://office.local/Shared%20Files/sub%20folder/",
            NetworkDrive.resource("office.local", List.of("Shared Files", "sub folder"), true));
        assertEquals("smb://192.168.1.4/Public/notes%23draft.txt",
            NetworkDrive.resource("192.168.1.4", List.of("Public", "notes#draft.txt"), false));
    }

    @Test public void rejectsAddressWithCredentialsOrPath() {
        for (String invalid : List.of("user@office", "office/share", "smb://office", "office:445")) {
            assertThrows(IllegalArgumentException.class, () -> NetworkDrive.serverAddress(invalid));
        }
    }

    @Test public void rejectsPathsThatCanEscapeTheSelectedFolder() {
        for (String invalid : List.of("..", ".", "a/b", "a\\b")) {
            assertThrows(IllegalArgumentException.class,
                () -> NetworkDrive.resource("office", List.of("Public", invalid), false));
        }
    }
}
