using DubboNet.Clients;
using DubboNet.Clients.DataModle;
using DubboNet.DubboService.DataModle;
using System.Linq.Expressions;
using System.Reflection;

namespace UnitTestForDubboNet
{
    public class DubboClientApiTest
    {
        [Fact]
        public void PublicRequestMethods_OnlyExposeQueryAsync()
        {
            MethodInfo[] requestMethods = typeof(DubboClient)
                .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
                .Where(method => method.Name.StartsWith("Query", StringComparison.Ordinal))
                .ToArray();

            Assert.Equal(10, requestMethods.Length);
            Assert.All(
                requestMethods,
                method => Assert.Equal(nameof(DubboClient.QueryAsync), method.Name));
            Assert.DoesNotContain(
                requestMethods,
                method => method.GetGenericArguments().Length > 1);
            Assert.DoesNotContain(
                requestMethods,
                method => method.GetParameters().Select(parameter => parameter.ParameterType)
                    .SequenceEqual(new[] { typeof(string), typeof(string) }));
        }

        [Fact]
        public void SingleArrayArgument_BindsToSingleArgumentOverload()
        {
            Expression<Func<DubboClient, Task<DubboRequestResult>>> stringArrayCall =
                client => client.QueryAsync("com.foo.DemoService#echo", new[] { "A", "B" });
            Expression<Func<DubboClient, Task<DubboRequestResult>>> objectArrayCall =
                client => client.QueryAsync(
                    "com.foo.DemoService#echo",
                    new object[] { "A", "B" });

            AssertSingleArgumentOverload(stringArrayCall);
            AssertSingleArgumentOverload(objectArrayCall);
        }

        [Fact]
        public void ExplicitJavaTypes_BindToExplicitSignatureOverload()
        {
            Expression<Func<DubboClient, Task<DubboRequestResult>>> call =
                client => client.QueryAsync(
                    "com.foo.DemoService#echo",
                    new[] { "java.lang.String" },
                    new object[] { "A" });

            MethodCallExpression methodCall = Assert.IsAssignableFrom<MethodCallExpression>(call.Body);
            ParameterInfo[] parameters = methodCall.Method.GetParameters();
            Assert.Equal(typeof(IReadOnlyList<string>), parameters[1].ParameterType);
            Assert.Equal(typeof(IReadOnlyList<object>), parameters[2].ParameterType);
        }

        private static void AssertSingleArgumentOverload(
            Expression<Func<DubboClient, Task<DubboRequestResult>>> expression)
        {
            MethodCallExpression methodCall = Assert.IsAssignableFrom<MethodCallExpression>(expression.Body);
            ParameterInfo[] parameters = methodCall.Method.GetParameters();
            Assert.Equal(2, parameters.Length);
            Assert.Equal(typeof(object), parameters[1].ParameterType);
        }
    }
}
