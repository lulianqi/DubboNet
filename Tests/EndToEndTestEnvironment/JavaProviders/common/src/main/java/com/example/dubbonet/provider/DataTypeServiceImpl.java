/*
 * Licensed to the Apache Software Foundation (ASF) under one or more
 * contributor license agreements. See the NOTICE file distributed with
 * this work for additional information regarding copyright ownership.
 * The ASF licenses this file to You under the Apache License, Version 2.0
 * (the "License"); you may not use this file except in compliance with
 * the License. You may obtain a copy of the License at
 *
 *   http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

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
