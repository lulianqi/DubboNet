package com.example.dubbonet.api;

public interface OverloadService {
    String overload();

    String overload(String value);

    String overload(int value);

    String overload(Integer value);

    String overload(String value, int repeat);
}
