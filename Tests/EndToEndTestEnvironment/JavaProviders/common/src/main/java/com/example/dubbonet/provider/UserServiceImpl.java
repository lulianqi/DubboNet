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

import com.example.dubbonet.api.UserService;
import com.example.dubbonet.model.CreateUserRequest;
import com.example.dubbonet.model.OperationResult;
import com.example.dubbonet.model.User;

import java.util.ArrayList;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;
import java.util.concurrent.ConcurrentHashMap;
import java.util.concurrent.ConcurrentMap;

public class UserServiceImpl implements UserService {
    private final ConcurrentMap<Long, Map<String, String>> profiles =
            new ConcurrentHashMap<Long, Map<String, String>>();

    @Override
    public User getUser(long userId) {
        if (userId <= 0) {
            return null;
        }
        User user = new User(userId, "user-" + userId, 18 + (int) (userId % 30), userId % 2 != 0);
        Map<String, String> stored = profiles.get(userId);
        if (stored == null) {
            stored = new LinkedHashMap<String, String>();
            stored.put("source", "dubbonet-demo");
            stored.put("level", userId % 2 == 0 ? "standard" : "vip");
        }
        user.setAttributes(new LinkedHashMap<String, String>(stored));
        return user;
    }

    @Override
    public List<User> listUsers(List<Long> userIds) {
        List<User> users = new ArrayList<User>();
        if (userIds == null) {
            return users;
        }
        for (Long userId : userIds) {
            if (userId != null) {
                User user = getUser(userId.longValue());
                if (user != null) {
                    users.add(user);
                }
            }
        }
        return users;
    }

    @Override
    public OperationResult<User> createUser(CreateUserRequest request) {
        if (request == null || request.getName() == null || request.getName().trim().isEmpty()) {
            return OperationResult.failure("INVALID_NAME", "name must not be blank");
        }
        long id = 10000L + Math.abs(request.getName().hashCode() % 90000);
        User user = new User(id, request.getName().trim(), request.getAge(), true);
        Map<String, String> attributes = new LinkedHashMap<String, String>();
        attributes.put("roles", request.getRoles() == null ? "[]" : request.getRoles().toString());
        attributes.put("createdBy", "createUser");
        user.setAttributes(attributes);
        return OperationResult.success(user);
    }

    @Override
    public Map<Long, User> batchGet(long[] userIds) {
        Map<Long, User> users = new LinkedHashMap<Long, User>();
        if (userIds == null) {
            return users;
        }
        for (long userId : userIds) {
            User user = getUser(userId);
            if (user != null) {
                users.put(userId, user);
            }
        }
        return users;
    }

    @Override
    public void updateProfile(long userId, Map<String, String> attributes) {
        if (userId <= 0) {
            throw new IllegalArgumentException("userId must be positive");
        }
        Map<String, String> copy = attributes == null
                ? new LinkedHashMap<String, String>()
                : new LinkedHashMap<String, String>(attributes);
        profiles.put(userId, copy);
    }
}
