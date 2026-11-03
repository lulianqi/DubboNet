package com.example.dubbonet.model;

import java.io.Serializable;
import java.util.LinkedHashMap;
import java.util.Map;

public class User implements Serializable {
    private static final long serialVersionUID = 1L;

    private long id;
    private String name;
    private int age;
    private boolean active;
    private Map<String, String> attributes = new LinkedHashMap<String, String>();

    public User() {
    }

    public User(long id, String name, int age, boolean active) {
        this.id = id;
        this.name = name;
        this.age = age;
        this.active = active;
    }

    public long getId() {
        return id;
    }

    public void setId(long id) {
        this.id = id;
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

    public boolean isActive() {
        return active;
    }

    public void setActive(boolean active) {
        this.active = active;
    }

    public Map<String, String> getAttributes() {
        return attributes;
    }

    public void setAttributes(Map<String, String> attributes) {
        this.attributes = attributes;
    }

    @Override
    public String toString() {
        return "User{" +
                "id=" + id +
                ", name='" + name + '\'' +
                ", age=" + age +
                ", active=" + active +
                ", attributes=" + attributes +
                '}';
    }
}
