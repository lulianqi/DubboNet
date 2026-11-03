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

using DubboNet.DubboService.DataModle;
using DubboNet.Testing;

namespace UnitTestForDubboNet;

public sealed class QueryDemoVerificationTest
{
    [Theory]
    [InlineData("\"pong\"", true)]
    [InlineData("\"unexpected\"", false)]
    [InlineData("null", false)]
    [InlineData("42", false)]
    [InlineData("not-json", false)]
    [InlineData("", false)]
    public void Json_verification_requires_the_expected_business_value(string payload, bool expected)
    {
        var result = new DubboRequestResult { Result = payload };
        Assert.Equal(expected, QueryDemoVerification.MatchesJson(result, json => json.GetString() == "pong"));
    }

    [Fact]
    public void Transport_failure_cannot_pass_even_with_an_expected_payload()
    {
        var result = new DubboRequestResult { Result = "\"pong\"", QuerySuccess = false };
        Assert.False(QueryDemoVerification.MatchesJson(result, json => json.GetString() == "pong"));
    }

    [Fact]
    public void Error_details_cannot_be_ignored()
    {
        var result = new DubboRequestResult { Result = "42", ErrorMeaasge = "timed out" };
        Assert.False(QueryDemoVerification.MatchesJson(result, json => json.GetInt32() == 42));
    }

    [Theory]
    [InlineData("42", true)]
    [InlineData("0", false)]
    [InlineData("\"not-an-integer\"", false)]
    public void Typed_verification_checks_the_value_and_deserialization(string payload, bool expected)
    {
        var result = new DubboRequestResult<int> { Result = payload };
        Assert.Equal(expected, QueryDemoVerification.MatchesTyped(result, 42));
    }

    [Fact]
    public void Failed_deserialization_is_not_a_successful_default_value()
    {
        var result = new DubboRequestResult<int> { Result = "\"not-an-integer\"" };
        Assert.False(QueryDemoVerification.MatchesTyped(result, 0));
        Assert.False(string.IsNullOrWhiteSpace(result.ErrorMeaasge));
    }

    [Fact]
    public void Missing_expected_map_keys_fail_validation()
    {
        var result = new DubboRequestResult { Result = "{}" };
        Assert.False(QueryDemoVerification.MatchesJson(result,
            json => json.GetProperty("traceId").GetString() == "query-demo"));
    }
}
