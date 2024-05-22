using DubboNet.DubboService;
using DubboNet.Clients;
using DubboNet.Clients.DataModle;
using static DubboNet.Clients.DubboClient;
using static DubboNet.DubboService.TelnetDubboActuatorSuite;
using Xunit.Abstractions;
using System.Reflection;
using DubboNet.DubboService.DataModle;

namespace UnitTestForDubboNet
{
    public class DubboServiceDriverTest
    {
        protected readonly ITestOutputHelper Output;

        public DubboServiceDriverTest(ITestOutputHelper outPut)
        {
            Output = outPut;
        }

        [Theory]
        [InlineData(LoadBalanceMode.Random)]
        [InlineData(LoadBalanceMode.RoundRobin)]
        [InlineData(LoadBalanceMode.ConsistentHash)]
        [InlineData(LoadBalanceMode.ShortestResponse)]
        public void GetDubboActuatorSuiteTest(LoadBalanceMode loadBalanceMode)
        {
            List<DubboServiceEndPointInfo> dubboServiceEndPointInfos = new List<DubboServiceEndPointInfo>();
            dubboServiceEndPointInfos.Add(new DubboServiceEndPointInfo(){EndPoint = new System.Net.IPEndPoint(System.Net.IPAddress.Parse("127.0.0.1"),1234), InnerDubboActuatorSuite = new TelnetDubboActuatorSuite(new System.Net.IPEndPoint(System.Net.IPAddress.Parse("127.0.0.1"),1234) , new DubboActuatorSuiteConf(){ IsAutoUpdateStatusInfo = false}) , Weight = 50});
            dubboServiceEndPointInfos.Add(new DubboServiceEndPointInfo(){EndPoint = new System.Net.IPEndPoint(System.Net.IPAddress.Parse("127.0.0.2"),1234), InnerDubboActuatorSuite = new TelnetDubboActuatorSuite(new System.Net.IPEndPoint(System.Net.IPAddress.Parse("127.0.0.2"),1234) , new DubboActuatorSuiteConf(){ IsAutoUpdateStatusInfo = false}) , Weight = 150 });
            dubboServiceEndPointInfos.Add(new DubboServiceEndPointInfo(){EndPoint = new System.Net.IPEndPoint(System.Net.IPAddress.Parse("127.0.0.3"),1234), InnerDubboActuatorSuite = new TelnetDubboActuatorSuite(new System.Net.IPEndPoint(System.Net.IPAddress.Parse("127.0.0.3"),1234) , new DubboActuatorSuiteConf(){ IsAutoUpdateStatusInfo = false}) });
            dubboServiceEndPointInfos.Add(new DubboServiceEndPointInfo(){EndPoint = new System.Net.IPEndPoint(System.Net.IPAddress.Parse("127.0.0.4"),1234), InnerDubboActuatorSuite = new TelnetDubboActuatorSuite(new System.Net.IPEndPoint(System.Net.IPAddress.Parse("127.0.0.4"),1234) , new DubboActuatorSuiteConf(){ IsAutoUpdateStatusInfo = false}) });
            dubboServiceEndPointInfos.Add(new DubboServiceEndPointInfo(){EndPoint = new System.Net.IPEndPoint(System.Net.IPAddress.Parse("127.0.0.5"),1234), InnerDubboActuatorSuite = new TelnetDubboActuatorSuite(new System.Net.IPEndPoint(System.Net.IPAddress.Parse("127.0.0.5"),1234) , new DubboActuatorSuiteConf(){ IsAutoUpdateStatusInfo = false}) });
            dubboServiceEndPointInfos.Add(new DubboServiceEndPointInfo(){EndPoint = new System.Net.IPEndPoint(System.Net.IPAddress.Parse("127.0.0.6"),1234), InnerDubboActuatorSuite = new TelnetDubboActuatorSuite(new System.Net.IPEndPoint(System.Net.IPAddress.Parse("127.0.0.6"),1234) , new DubboActuatorSuiteConf(){ IsAutoUpdateStatusInfo = false}) });
            dubboServiceEndPointInfos.Add(new DubboServiceEndPointInfo(){EndPoint = new System.Net.IPEndPoint(System.Net.IPAddress.Parse("127.0.0.7"),1234), InnerDubboActuatorSuite = new TelnetDubboActuatorSuite(new System.Net.IPEndPoint(System.Net.IPAddress.Parse("127.0.0.7"),1234) , new DubboActuatorSuiteConf(){ IsAutoUpdateStatusInfo = false}) });
            dubboServiceEndPointInfos.Add(new DubboServiceEndPointInfo(){EndPoint = new System.Net.IPEndPoint(System.Net.IPAddress.Parse("127.0.0.8"),1234), InnerDubboActuatorSuite = new TelnetDubboActuatorSuite(new System.Net.IPEndPoint(System.Net.IPAddress.Parse("127.0.0.8"),1234) , new DubboActuatorSuiteConf(){ IsAutoUpdateStatusInfo = false}) });
            dubboServiceEndPointInfos.Add(new DubboServiceEndPointInfo(){EndPoint = new System.Net.IPEndPoint(System.Net.IPAddress.Parse("127.0.0.9"),1234), InnerDubboActuatorSuite = new TelnetDubboActuatorSuite(new System.Net.IPEndPoint(System.Net.IPAddress.Parse("127.0.0.9"),1234) , new DubboActuatorSuiteConf(){ IsAutoUpdateStatusInfo = false}) });
            dubboServiceEndPointInfos.Add(new DubboServiceEndPointInfo(){EndPoint = new System.Net.IPEndPoint(System.Net.IPAddress.Parse("127.0.0.10"),1234), InnerDubboActuatorSuite = new TelnetDubboActuatorSuite(new System.Net.IPEndPoint(System.Net.IPAddress.Parse("127.0.0.10"),1234) , new DubboActuatorSuiteConf(){ IsAutoUpdateStatusInfo = false}) });
            dubboServiceEndPointInfos.Add(new DubboServiceEndPointInfo(){EndPoint = new System.Net.IPEndPoint(System.Net.IPAddress.Parse("127.0.0.11"),1234), InnerDubboActuatorSuite = new TelnetDubboActuatorSuite(new System.Net.IPEndPoint(System.Net.IPAddress.Parse("127.0.0.11"),1234) , new DubboActuatorSuiteConf(){ IsAutoUpdateStatusInfo = false}) });


            DubboServiceDriver dubboServiceDriver = new("UnitTester",dubboServiceEndPointInfos,new Dictionary<System.Net.IPEndPoint, DubboActuatorSuiteEndPintInfo>());

            if (loadBalanceMode == LoadBalanceMode.ShortestResponse)
            {
                for (int i = 0; i < dubboServiceEndPointInfos.Count - 2; i++)
                {
                    //SetDubboActuatorSuiteStatusByReflection(dubboServiceEndPointInfos[i].InnerDubboActuatorSuite, i);
                    SetDubboActuatorSuiteStatusByReflection(dubboServiceDriver.InnerActuatorSuites[dubboServiceEndPointInfos[i].EndPoint].InnerDubboActuatorSuite, i);
                }
            }

            for (int i = 0;i<100;i++)
            {
                IDubboActuatorSuite dubboActuatorSuite = dubboServiceDriver.GetDubboActuatorSuite(loadBalanceMode , i.ToString());
                Output.WriteLine(dubboActuatorSuite.ToString());
                Assert.NotNull(dubboActuatorSuite);
            }
            //Output only shows if the test fails. (in vs code)
            //Assert.False(true);
        }


        private void SetDubboActuatorSuiteStatusByReflection(IDubboActuatorSuite dubboActuatorSuite ,int lastQueryElapsed)
        {
            // 获取类型信息
            Type type = dubboActuatorSuite.GetType();

            // 获取私有字段信息
            PropertyInfo propertyInfo = type.GetProperty("ActuatorSuiteStatusInfo", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

            if (propertyInfo != null && propertyInfo.CanWrite)
            {
                // 设置新的值
                DubboActuatorSuiteStatus newValue = new DubboActuatorSuiteStatus() { LastQueryElapsed = lastQueryElapsed}; // 假设这是你要设置的新值
                propertyInfo.SetValue(dubboActuatorSuite, newValue);
            }
        }
    }
}
