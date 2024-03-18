using DubboNet.DubboService;
using DubboNet.Clients;

namespace UnitTestForDubboNet
{
    public class DubboServiceDriverTest
    {
        [Theory]
        [InlineData(@"com.xxxxx.pay.api.PayRemoteService")]
        [InlineData("PROVIDER:\r\ncom.xxxxx.pay.api.PayRemoteService\r\ncom.xxxxx.service.api.WorkOrderProductRemoteService\r\ncom.xxxxx.service.api.UserAccountRemoteService\r\ncom.xxxxx.service.api.PhoneHomeLocationRemoteService\r\nCONSUMER:\r\ncom.xxx.decision.api.record.MiniAppRecordReadRemoteServicecom.xxx.decision.api.dialoguebot.globalstrategy.TBotGlobalStrategyKnowledgeReadRemote")]
        public void DubboLsInfoTest(string source)
        {
            DubboServiceDriver dubboServiceDriver = new();
        }
    }
}
