package com.example.dubbonet.api;

import com.example.dubbonet.model.CreateUserRequest;
import com.example.dubbonet.model.OperationResult;
import com.example.dubbonet.model.User;

import java.util.List;
import java.util.Map;

public interface UserService {
    User getUser(long userId);

    List<User> listUsers(List<Long> userIds);

    OperationResult<User> createUser(CreateUserRequest request);

    Map<Long, User> batchGet(long[] userIds);

    void updateProfile(long userId, Map<String, String> attributes);
}
