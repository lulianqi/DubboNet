package com.example.dubbonet.provider;

import com.example.dubbonet.api.DataTypeService;

import java.util.Date;

public class DataTypeServiceImpl implements DataTypeService {
    @Override
    public int add(int left, int right) {
        return left + right;
    }

    @Override
    public double average(double[] values) {
        if (values == null || values.length == 0) {
            return Double.NaN;
        }
        double sum = 0D;
        for (double value : values) {
            sum += value;
        }
        return sum / values.length;
    }

    @Override
    public boolean negate(boolean value) {
        return !value;
    }

    @Override
    public Date serverTime() {
        return new Date();
    }

    @Override
    public String join(String separator, String[] values) {
        if (values == null) {
            return null;
        }
        String actualSeparator = separator == null ? "" : separator;
        StringBuilder result = new StringBuilder();
        for (int i = 0; i < values.length; i++) {
            if (i > 0) {
                result.append(actualSeparator);
            }
            result.append(values[i] == null ? "<null>" : values[i]);
        }
        return result.toString();
    }
}
