package com.cosmerearchives.homebridge;

import java.io.OutputStream;
import java.util.List;
import java.util.function.LongConsumer;

interface FileSource extends AutoCloseable {
    List<NetworkDrive.Entry> browse(List<String> path) throws Exception;
    long copy(List<String> path, OutputStream destination, LongConsumer progress) throws Exception;
}
