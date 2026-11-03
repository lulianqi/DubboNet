package com.example.dubbonet.model;

import java.io.Serializable;
import java.util.ArrayList;
import java.util.List;

public class CreateUserRequest implements Serializable {
    private static final long serialVersionUID = 1L;

    private String name;
    private int age;
    private List<String> roles = new ArrayList<String>();

    public CreateUserRequest() {
    }

    public CreateUserRequest(String name, int age, List<String> roles) {
        this.name = name;
        this.age = age;
        this.roles = roles;
    }

    public String getName() {
        return name;
    }

    public void setName(String name) {
        this.name = name;
    }

    public int getAge() {
        return age;
    }

    public void setAge(int age) {
        this.age = age;
    }

    public List<String> getRoles() {
        return roles;
    }

    public void setRoles(List<String> roles) {
        this.roles = roles;
    }

    @Override
    public String toString() {
        return "CreateUserRequest{" +
                "name='" + name + '\'' +
                ", age=" + age +
                ", roles=" + roles +
                '}';
    }
}
