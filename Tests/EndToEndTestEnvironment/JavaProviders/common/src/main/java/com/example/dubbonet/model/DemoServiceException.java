package com.example.dubbonet.model;

public class DemoServiceException extends RuntimeException {
    private static final long serialVersionUID = 1L;

    private String errorCode;

    public DemoServiceException() {
    }

    public DemoServiceException(String errorCode, String message) {
        super(message);
        this.errorCode = errorCode;
    }

    public String getErrorCode() {
        return errorCode;
    }

    public void setErrorCode(String errorCode) {
        this.errorCode = errorCode;
    }
}
