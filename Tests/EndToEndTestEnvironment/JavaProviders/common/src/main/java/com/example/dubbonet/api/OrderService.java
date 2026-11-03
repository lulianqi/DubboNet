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

package com.example.dubbonet.api;

import com.example.dubbonet.model.CreateOrderRequest;
import com.example.dubbonet.model.OperationResult;
import com.example.dubbonet.model.Order;
import com.example.dubbonet.model.OrderItem;
import com.example.dubbonet.model.OrderQuery;
import com.example.dubbonet.model.OrderStatus;
import com.example.dubbonet.model.PageResult;

import java.math.BigDecimal;
import java.util.List;

public interface OrderService {
    Order getOrder(String orderId);

    List<Order> listOrders(long userId);

    PageResult<Order> searchOrders(OrderQuery query);

    BigDecimal calculateTotal(List<OrderItem> items);

    OperationResult<OrderStatus> updateStatus(String orderId, OrderStatus status);
}
