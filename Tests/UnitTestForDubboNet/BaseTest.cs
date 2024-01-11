using DubboNet.DubboService;
using DubboNet.DubboService.DataModle.DubboInfo;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using static DubboNet.DubboService.DubboActuator;

namespace UnitTestForDubboNet
{
    public class BaseTest
    {
        [Theory]
        [InlineData(typeof(sbyte),true)]
        [InlineData(typeof(byte), true)]
        [InlineData(typeof(short), true)]
        [InlineData(typeof(ushort), true)]
        [InlineData(typeof(int), true)]
        [InlineData(typeof(uint), true)]
        [InlineData(typeof(long), true)]
        [InlineData(typeof(ulong), true)]
        [InlineData(typeof(char), true)]
        [InlineData(typeof(float), true)]
        [InlineData(typeof(double), true)]
        [InlineData(typeof(bool), true)]
        [InlineData(typeof(decimal), true)]
        [InlineData(typeof(string), true)]
        [InlineData(typeof(DateTime), true)]
        [InlineData(typeof(DubboActuatorStatus), true)]
        [InlineData(typeof(DubboFuncInfo), false)]
        [InlineData(typeof(DubboActuator), false)]
        [InlineData(typeof(List<>), false)]
        [InlineData(typeof(Array), false)]
        [InlineData(typeof(Object), false)]
        [InlineData(typeof(Point), false)]
        [InlineData(typeof(IEnumerable<>), false)]
        [InlineData(typeof(IDictionary), false)]
        public void IsSimpleTest(Type type , bool expect)
        {
            Assert.Equal<bool>( DubboActuator.IsSimple(type), expect);
        }

        [Theory]
        [InlineData(@"com.xxxxx.pay.api.PayRemoteService")]
        [InlineData("PROVIDER:\r\ncom.xxxxx.pay.api.PayRemoteService\r\ncom.xxxxx.service.api.WorkOrderProductRemoteService\r\ncom.xxxxx.service.api.UserAccountRemoteService\r\ncom.xxxxx.service.api.PhoneHomeLocationRemoteService\r\nCONSUMER:\r\ncom.xxx.decision.api.record.MiniAppRecordReadRemoteServicecom.xxx.decision.api.dialoguebot.globalstrategy.TBotGlobalStrategyKnowledgeReadRemote")]
        public void DubboLsInfoTest(string source)
        {
            Assert.Equal("com.xxxxx.pay.api.PayRemoteService",DubboLsInfo.GetDubboLsInfo(source).Providers[0]);
        }
    }
}