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
