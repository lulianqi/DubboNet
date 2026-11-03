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

package com.example.dubbonet.model;

import java.io.Serializable;
import java.util.ArrayList;
import java.util.List;

public class CreateOrderRequest implements Serializable {
    private static final long serialVersionUID = 1L;

    private long userId;
    private List<OrderItem> items = new ArrayList<OrderItem>();
    private String remark;

    public CreateOrderRequest() {
    }

    public CreateOrderRequest(long userId, List<OrderItem> items, String remark) {
        this.userId = userId;
        this.items = items;
        this.remark = remark;
    }

    public long getUserId() {
        return userId;
    }

    public void setUserId(long userId) {
        this.userId = userId;
    }

    public List<OrderItem> getItems() {
        return items;
    }

    public void setItems(List<OrderItem> items) {
        this.items = items;
    }

    public String getRemark() {
        return remark;
    }

    public void setRemark(String remark) {
        this.remark = remark;
    }

    @Override
    public String toString() {
        return "CreateOrderRequest{" +
                "userId=" + userId +
                ", items=" + items +
                ", remark='" + remark + '\'' +
                '}';
    }
}
