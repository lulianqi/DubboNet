package com.example.dubbonet.provider;

import com.example.dubbonet.api.OverloadService;

public class OverloadServiceImpl implements OverloadService {
    @Override
    public String overload() {
        return "overload()";
    }

    @Override
    public String overload(String value) {
        return "overload(String):" + String.valueOf(value);
    }

    @Override
    public String overload(int value) {
        return "overload(int):" + value;
    }

    @Override
    public String overload(Integer value) {
        return "overload(Integer):" + String.valueOf(value);
    }

    @Override
    public String overload(String value, int repeat) {
        StringBuilder result = new StringBuilder("overload(String,int):");
        for (int index = 0; index < Math.max(0, repeat); index++) {
            if (index > 0) {
                result.append('|');
            }
            result.append(String.valueOf(value));
        }
        return result.toString();
    }
}
