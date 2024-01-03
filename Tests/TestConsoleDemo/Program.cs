// See https://aka.ms/new-console-template for more information
using DubboNet.DubboService;
using DubboNet.DubboService.DataModle;
using System.Text.Encodings.Web;
using System.Text.Json;
using TestConsoleDemo.DataModle;

DubboActuatorSuite dubboActuatorSuite = new DubboActuatorSuite("0.0.0.0", 0);
dubboActuatorSuite.Dispose();

Console.WriteLine("TestDemoConsole");
DubboActuator dubboActuator = new DubboActuator("10.100.64.182", 20889);
DubboRequestResult requestResult = await dubboActuator.SendQuery("com.byai.line.api.voip.VoipCallerAccountRemoteService.getAllFreeswitchs");
Console.WriteLine(requestResult.Result);
DubboRequestResult<GetAllFsDataModle> DrModle = await dubboActuator.SendQuery<GetAllFsDataModle>("com.byai.line.api.voip.VoipCallerAccountRemoteService.getAllFreeswitchs");
var option = new JsonSerializerOptions()
{
    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
};
Console.WriteLine(JsonSerializer.Serialize(DrModle.ResultModle, option));
DubboRequestResult<PlainResult> DrModle_PlainResult = await dubboActuator.SendQuery<PlainResult,string>("com.byai.line.api.voip.VoipCallerAccountRemoteService.getVoipAccountGw","1");
Console.WriteLine(JsonSerializer.Serialize(DrModle_PlainResult.ResultModle, option));
DrModle_PlainResult = await dubboActuator.SendQuery<PlainResult, string>("com.byai.line.api.voip.VoipCallerAccountRemoteService.getVoipAccountGw", "测试");
Console.WriteLine(JsonSerializer.Serialize(DrModle_PlainResult.ResultModle, option));
Console.ReadLine();
