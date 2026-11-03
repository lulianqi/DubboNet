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

import com.example.dubbonet.api.OrderService;
import com.example.dubbonet.model.CreateOrderRequest;
import com.example.dubbonet.model.OperationResult;
import com.example.dubbonet.model.Order;
import com.example.dubbonet.model.OrderItem;
import com.example.dubbonet.model.OrderQuery;
import com.example.dubbonet.model.OrderStatus;
import com.example.dubbonet.model.PageResult;

import java.math.BigDecimal;
import java.util.ArrayList;
import java.util.Date;
import java.util.List;

public class OrderServiceImpl implements OrderService {
    private static final long BASE_TIME_MILLIS = 1704067200000L;

    @Override
    public Order getOrder(String orderId) {
        if (orderId == null || orderId.trim().isEmpty()) {
            return null;
        }
        long userId = 1000L + Math.abs(orderId.hashCode() % 1000);
        List<OrderItem> items = sampleItems();
        return new Order(orderId, userId, items, calculateTotal(items), OrderStatus.PAID,
                new Date(BASE_TIME_MILLIS + Math.abs(orderId.hashCode() % 86400000)));
    }

    @Override
    public List<Order> listOrders(long userId) {
        List<Order> orders = new ArrayList<Order>();
        for (int i = 1; i <= 3; i++) {
            Order order = getOrder("ORDER-" + userId + "-" + i);
            order.setUserId(userId);
            order.setStatus(OrderStatus.values()[i - 1]);
            orders.add(order);
        }
        return orders;
    }

    @Override
    public PageResult<Order> searchOrders(OrderQuery query) {
        int pageNo = query == null || query.getPageNo() < 1 ? 1 : query.getPageNo();
        int pageSize = query == null || query.getPageSize() < 1 ? 10 : Math.min(query.getPageSize(), 20);
        long userId = query == null || query.getUserId() == null ? 1001L : query.getUserId().longValue();
        List<Order> all = listOrders(userId);
        List<Order> filtered = new ArrayList<Order>();
        for (Order order : all) {
            boolean statusMatches = query == null || query.getStatuses() == null || query.getStatuses().isEmpty()
                    || query.getStatuses().contains(order.getStatus());
            boolean amountMatches = query == null || query.getMinimumAmount() == null
                    || order.getTotalAmount().compareTo(query.getMinimumAmount()) >= 0;
            if (statusMatches && amountMatches) {
                filtered.add(order);
            }
        }
        int fromIndex = Math.min((pageNo - 1) * pageSize, filtered.size());
        int toIndex = Math.min(fromIndex + pageSize, filtered.size());
        return new PageResult<Order>(new ArrayList<Order>(filtered.subList(fromIndex, toIndex)),
                pageNo, pageSize, filtered.size());
    }

    @Override
    public BigDecimal calculateTotal(List<OrderItem> items) {
        BigDecimal total = BigDecimal.ZERO;
        if (items == null) {
            return total;
        }
        for (OrderItem item : items) {
            if (item != null && item.getUnitPrice() != null) {
                total = total.add(item.getUnitPrice().multiply(BigDecimal.valueOf(item.getQuantity())));
            }
        }
        return total.setScale(2, BigDecimal.ROUND_HALF_UP);
    }

    @Override
    public OperationResult<OrderStatus> updateStatus(String orderId, OrderStatus status) {
        if (orderId == null || orderId.trim().isEmpty()) {
            return OperationResult.failure("INVALID_ORDER_ID", "orderId must not be blank");
        }
        if (status == null) {
            return OperationResult.failure("INVALID_STATUS", "status must not be null");
        }
        return OperationResult.success(status);
    }

    private List<OrderItem> sampleItems() {
        List<OrderItem> items = new ArrayList<OrderItem>();
        items.add(new OrderItem("SKU-001", "keyboard", 1, new BigDecimal("299.00")));
        items.add(new OrderItem("SKU-002", "cable", 2, new BigDecimal("29.90")));
        return items;
    }
}
