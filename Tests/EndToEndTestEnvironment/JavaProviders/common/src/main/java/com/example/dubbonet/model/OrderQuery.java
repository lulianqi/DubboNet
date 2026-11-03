package com.example.dubbonet.model;

import java.io.Serializable;
import java.math.BigDecimal;
import java.util.ArrayList;
import java.util.List;

public class OrderQuery implements Serializable {
    private static final long serialVersionUID = 1L;

    private Long userId;
    private List<OrderStatus> statuses = new ArrayList<OrderStatus>();
    private BigDecimal minimumAmount;
    private int pageNo = 1;
    private int pageSize = 10;

    public OrderQuery() {
    }

    public Long getUserId() {
        return userId;
    }

    public void setUserId(Long userId) {
        this.userId = userId;
    }

    public List<OrderStatus> getStatuses() {
        return statuses;
    }

    public void setStatuses(List<OrderStatus> statuses) {
        this.statuses = statuses;
    }

    public BigDecimal getMinimumAmount() {
        return minimumAmount;
    }

    public void setMinimumAmount(BigDecimal minimumAmount) {
        this.minimumAmount = minimumAmount;
    }

    public int getPageNo() {
        return pageNo;
    }

    public void setPageNo(int pageNo) {
        this.pageNo = pageNo;
    }

    public int getPageSize() {
        return pageSize;
    }

    public void setPageSize(int pageSize) {
        this.pageSize = pageSize;
    }

    @Override
    public String toString() {
        return "OrderQuery{" +
                "userId=" + userId +
                ", statuses=" + statuses +
                ", minimumAmount=" + minimumAmount +
                ", pageNo=" + pageNo +
                ", pageSize=" + pageSize +
                '}';
    }
}
