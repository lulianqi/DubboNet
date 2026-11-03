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
using System.Text.Json;

namespace DubboNet.Testing;

// Shared by QueryDemo and offline regression tests; not part of the client library.
internal static class QueryDemoVerification
{
    public static bool MatchesJson(DubboRequestResult result, Func<JsonElement, bool> predicate)
    {
        if (!Succeeded(result) || string.IsNullOrWhiteSpace(result.Result))
            return false;

        try
        {
            using JsonDocument document = JsonDocument.Parse(result.Result);
            return predicate(document.RootElement);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException
            or KeyNotFoundException or FormatException or OverflowException)
        {
            return false;
        }
    }

    public static bool MatchesTyped<T>(DubboRequestResult<T> result, T expected)
    {
        if (!result.QuerySuccess)
            return false;

        // Deserialization can set ErrorMeaasge lazily; check it after reading ResultModle.
        T actual = result.ResultModle;
        return Succeeded(result) && EqualityComparer<T>.Default.Equals(actual, expected);
    }

    private static bool Succeeded(DubboRequestResult result) =>
        result.QuerySuccess && string.IsNullOrWhiteSpace(result.ErrorMeaasge);
}
