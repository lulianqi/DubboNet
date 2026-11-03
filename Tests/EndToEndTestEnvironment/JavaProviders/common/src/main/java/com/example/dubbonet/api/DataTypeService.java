package com.example.dubbonet.api;

import java.util.Date;

public interface DataTypeService {
    int add(int left, int right);

    double average(double[] values);

    boolean negate(boolean value);

    Date serverTime();

    String join(String separator, String[] values);
}
