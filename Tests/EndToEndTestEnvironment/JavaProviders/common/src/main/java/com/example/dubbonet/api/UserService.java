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
