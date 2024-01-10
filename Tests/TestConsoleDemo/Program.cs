// See https://aka.ms/new-console-template for more information
using DubboNet.DubboService;
using DubboNet.DubboService.DataModle;
using System.Text.Encodings.Web;
using System.Text.Json;
using TestConsoleDemo.DataModle;



Console.WriteLine("TestDemoConsole");
TestForTimer();
Console.ReadLine();

static void TestForTimer()
{
    DubboActuatorSuite dubboActuatorSuite1 = new DubboActuatorSuite("0.0.0.1", 0);
    DubboActuatorSuite dubboActuatorSuite2 = new DubboActuatorSuite("0.0.0.2", 0);
    DubboActuatorSuite dubboActuatorSuite3 = new DubboActuatorSuite("0.0.0.3", 0);
    Console.WriteLine("enter to stop timer");
    Console.ReadLine();
}
static void TestForDispose()
{
    DubboActuatorSuite dubboActuatorSuite = new DubboActuatorSuite("0.0.0.0", 0);
    dubboActuatorSuite.Dispose();
}


static async Task TestForSendQuery()
{
    DubboActuator dubboActuator = new DubboActuator("10.100.64.182", 20889);
    DubboRequestResult requestResult = await dubboActuator.SendQuery("com.byai.line.api.voip.VoipCallerAccountRemoteService.getAllFreeswitchs");
    Console.WriteLine(requestResult.Result);
    DubboRequestResult<GetAllFsDataModle> DrModle = await dubboActuator.SendQuery<GetAllFsDataModle>("com.byai.line.api.voip.VoipCallerAccountRemoteService.getAllFreeswitchs");
    var option = new JsonSerializerOptions()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
    Console.WriteLine(JsonSerializer.Serialize(DrModle.ResultModle, option));
    DubboRequestResult<PlainResult> DrModle_PlainResult = await dubboActuator.SendQuery<PlainResult, string>("com.byai.line.api.voip.VoipCallerAccountRemoteService.getVoipAccountGw", "1");
    Console.WriteLine(JsonSerializer.Serialize(DrModle_PlainResult.ResultModle, option));
    DrModle_PlainResult = await dubboActuator.SendQuery<PlainResult, string>("com.byai.line.api.voip.VoipCallerAccountRemoteService.getVoipAccountGw", "测试");
    Console.WriteLine(JsonSerializer.Serialize(DrModle_PlainResult.ResultModle, option));

}