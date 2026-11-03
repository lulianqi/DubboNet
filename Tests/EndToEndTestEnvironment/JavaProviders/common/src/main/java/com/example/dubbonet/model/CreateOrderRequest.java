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
