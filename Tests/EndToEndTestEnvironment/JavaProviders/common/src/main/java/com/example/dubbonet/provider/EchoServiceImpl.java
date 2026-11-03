package com.example.dubbonet.provider;

import com.example.dubbonet.api.EchoService;
import com.example.dubbonet.model.DemoServiceException;

import java.util.LinkedHashMap;
import java.util.Map;

public class EchoServiceImpl implements EchoService {
    @Override
    public String ping() {
        return "pong";
    }

    @Override
    public String echo(String message) {
        return message;
    }

    @Override
    public byte[] echoBytes(byte[] payload) {
        return payload == null ? null : payload.clone();
    }

    @Override
    public Map<String, String> echoHeaders(Map<String, String> headers) {
        return headers == null
                ? new LinkedHashMap<String, String>()
                : new LinkedHashMap<String, String>(headers);
    }

    @Override
    public String alwaysFail(String message) throws DemoServiceException {
        throw new DemoServiceException("DEMO_FAILURE", message == null ? "expected failure" : message);
    }
}
