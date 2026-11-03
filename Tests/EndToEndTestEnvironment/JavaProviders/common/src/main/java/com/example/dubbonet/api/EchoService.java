package com.example.dubbonet.api;

import com.example.dubbonet.model.DemoServiceException;

import java.util.Map;

public interface EchoService {
    String ping();

    String echo(String message);

    byte[] echoBytes(byte[] payload);

    Map<String, String> echoHeaders(Map<String, String> headers);

    String alwaysFail(String message) throws DemoServiceException;
}
