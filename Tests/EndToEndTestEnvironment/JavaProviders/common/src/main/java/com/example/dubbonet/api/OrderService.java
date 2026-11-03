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
