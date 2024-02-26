// See https://aka.ms/new-console-template for more information
using DubboNet.Clients;
using DubboNet.Clients.RegistryClient;
using DubboNet.DubboService;
using DubboNet.DubboService.DataModle;
using NetService.Telnet;
using System.Diagnostics;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using TestConsoleDemo.DataModle;


Console.WriteLine("TestDemoConsole");
CasTest();
//await TestForExTelnet();
//await SendHttpRequestAsync(new Uri("http://baidu.com"));
//await StressTestForDubboClient();
//await TestForFinalize();
Console.WriteLine("Enter to Exit");
Console.ReadLine();
for (int i = 0; i < 10; i++)
{
    //GC.Collect();
    GC.Collect(2, GCCollectionMode.Forced);
    GC.WaitForPendingFinalizers();
    Console.ReadLine();
}

static async Task SendHttpRequestAsync(Uri? uri = null, int port = 80, CancellationToken cancellationToken = default)
{
    uri ??= new Uri("http://example.com");

    // Construct a minimalistic HTTP/1.1 request
    byte[] requestBytes = Encoding.ASCII.GetBytes(@$"GET {uri.AbsoluteUri} HTTP/1.1
Host: {uri.Host}
Connection: Close

");

    // Create and connect a dual-stack socket
    using Socket socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
    await socket.ConnectAsync(uri.Host, port, cancellationToken);

    // Send the request.
    // For the tiny amount of data in this example, the first call to SendAsync() will likely deliver the buffer completely,
    // however this is not guaranteed to happen for larger real-life buffers.
    // The best practice is to iterate until all the data is sent.
    int bytesSent = 0;
    while (bytesSent < requestBytes.Length)
    {
        bytesSent += await socket.SendAsync(requestBytes.AsMemory(bytesSent), SocketFlags.None);
    }

    // Do minimalistic buffering assuming ASCII response
    byte[] responseBytes = new byte[256];
    char[] responseChars = new char[256];

    while (true)
    {
        int bytesReceived = await socket.ReceiveAsync(responseBytes, SocketFlags.None, cancellationToken);

        // Receiving 0 bytes means EOF has been reached
        if (bytesReceived == 0) break;

        // Convert byteCount bytes to ASCII characters using the 'responseChars' buffer as destination
        int charCount = Encoding.ASCII.GetChars(responseBytes, 0, bytesReceived, responseChars, 0);

        // Print the contents of the 'responseChars' buffer to Console.Out
        await Console.Out.WriteAsync(responseChars.AsMemory(0, charCount), cancellationToken);

        Console.WriteLine("e to Exit");
        if(Console.ReadLine()=="e")
        {
            break;
        }
    }
    socket.Dispose();
}

static async Task TestForFinalize()
{
    Console.WriteLine("Enter to start TestForFinalize");
    Console.ReadLine();
    DubboActuator dubboActuator = new DubboActuator("10.100.64.181", 7100);
    DubboActuator dubboActuator2 = new DubboActuator("10.100.64.181", 7100);
    DubboActuator dubboActuator3 = new DubboActuator("10.100.64.181", 7100);
    await dubboActuator.Connect();
    await dubboActuator2.Connect();
    await dubboActuator3.Connect();
    Console.WriteLine($"NowErrorMes:{dubboActuator.NowErrorMes}");
    Console.ReadLine();
    dubboActuator.Dispose(); 
    dubboActuator2.Dispose();
    dubboActuator3.Dispose();
}

static async Task TestForExTelnet()
{
    Console.WriteLine("Enter to start TestForExTelnet");
    Console.ReadLine();
    //using (var evt = new AutoResetEvent(false))
    //{
    //    evt.WaitOne(2000);
    //    Console.WriteLine("Enter to end using");
    //    Console.ReadLine();
    //}
    ExTelnet tl = new ExTelnet("10.100.64.181", 7100);
    await tl.ConnectAsync();
    //ExTelnet t2 = new ExTelnet("10.100.64.181", 7100);
    //await t2.ConnectAsync();
    //ExTelnet t3 = new ExTelnet("10.100.64.181", 7100);
    //await t3.ConnectAsync();
    Console.WriteLine($"NowErrorMes:{tl.NowErrorMes}");
    Console.ReadLine();
    tl.Dispose();
    //t2.Dispose();
    //t3.Dispose();
}

static async Task StressTestForDubboClient()
{
    Console.WriteLine("Enter to start StressTestForDubboClient");
    Console.ReadLine();
    var dubboClient  =new DubboClient("10.100.64.198:2181" , new DubboClient.DubboClientConf() { DubboActuatorSuiteMasterConnectionAliveTime = 600 }) ;
    List<Task<DubboRequestResult>> tasks = new List<Task<DubboRequestResult>>();
    Stopwatch stopwatch = new Stopwatch();
    stopwatch.Start();
    for(int i =0;i<50; i++)
    {
        Task<DubboRequestResult> task = dubboClient.SendRequestAsync("com.byai.saas.callcenter.api.CsStaffRemoteService.getCsStaffServiceTime", "123392,1939");
        tasks.Add(task);
    }
    await Task.WhenAll(tasks);
    stopwatch.Stop();
    Console.WriteLine($"StressTestForDubboClient ElapsedMilliseconds: {stopwatch.ElapsedMilliseconds}ms");
    Console.WriteLine("Enter to show DubboRequestResult");
    Console.ReadLine();
    foreach(var task in tasks)
    {
        Console.WriteLine("-------------------");
        Console.WriteLine(task.Result.ToString());
    }
    dubboClient.Dispose();
}

static async Task TestForDubboClient()
{
    var dubboClient  =new DubboClient("10.100.64.198:2181") ;
    var result = await dubboClient.SendRequestAsync("com.byai.saas.callcenter.api.CsStaffRemoteService.getCsStaffServiceTime","123392,1939");
    for(int i =0;i<10; i++)
    {
        _ = dubboClient.SendRequestAsync("com.byai.saas.callcenter.api.CsStaffRemoteService.getCsStaffServiceTime", "123392,1939").ContinueWith(rs => Console.WriteLine(rs.Result.Result));
    }
    dubboClient = null;
    Console.WriteLine(result.Result);
}

static async Task TestForOverZookeeper()
{
    MyZookeeper _innerMyZookeeper = new MyZookeeper("10.100.64.53:2181");
    //await _innerMyZookeeper.ConnectZooKeeperAsync();
    //var sta = await _innerMyZookeeper.ExistsAsync("/test_a/test_a_1", new MyWatcher("/test_a/test_a_1"));
    MyWatcher wc = new MyWatcher("wc");
    await _innerMyZookeeper.GetChildrenAsync("/test_a/test_a_1", wc);
    await _innerMyZookeeper.GetChildrenAsync("/test_a/test_a_2", wc);
    Console.ReadLine();
    //await _innerMyZookeeper.zooKeeper.getChildrenAsync("/test_a/test_a_2", wc);
    await _innerMyZookeeper.GetChildrenAsync("/test_a/test_a_2", wc);
    Console.WriteLine("Enter End TestForOverZookeeper");
    Console.ReadLine();
}

static async Task TestForOverWriter()
{
    //https://www.cnblogs.com/shanfeng1000/p/12700296.html
    DubboActuatorSuite dubboActuatorSuite = new DubboActuatorSuite("0.0.0.1", 0);
    await dubboActuatorSuite.SendQuery("1","1");
}

static async Task TestForReset()
{
    //AutoResetEvent sendQueryAutoResetEvent = new AutoResetEvent(false);
    ManualResetEvent sendQueryAutoResetEvent = new ManualResetEvent(false);
    _= Task.Factory.StartNew(()=>{Console.WriteLine("an task satrt 1");sendQueryAutoResetEvent.WaitOne();Console.WriteLine("an task end 1");});
    _= Task.Factory.StartNew(()=>{Console.WriteLine("an task satrt 2");sendQueryAutoResetEvent.WaitOne();Console.WriteLine("an task end 2");});
    _= Task.Factory.StartNew(()=>{Console.WriteLine("an task satrt 3");sendQueryAutoResetEvent.WaitOne();Console.WriteLine("an task end 3");});
    _= Task.Factory.StartNew(()=>{Console.WriteLine("an task satrt 4");sendQueryAutoResetEvent.WaitOne();Console.WriteLine("an task end 4");});
    _= Task.Factory.StartNew(()=>{Console.WriteLine("an task satrt 5");sendQueryAutoResetEvent.WaitOne();Console.WriteLine("an task end 5");});
    _= Task.Factory.StartNew(()=>{Console.WriteLine("an task satrt 6");sendQueryAutoResetEvent.WaitOne();Console.WriteLine("an task end 6");});
    await Task.Delay(5000);
    Console.WriteLine("ResetEvent.Set");
    sendQueryAutoResetEvent.Set();
    await Task.Delay(5000);
    Console.WriteLine("ResetEvent.Set");
    sendQueryAutoResetEvent.Set();
    await Task.Delay(5000);
    Console.WriteLine("ResetEvent.Set");
    sendQueryAutoResetEvent.Set();
}

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

static void CasTest()
{
    Cas cas = new Cas();
    Cas cas1 = new Cas();
    Cas cas2 = new Cas();
}
public class Cas
{
    static string Name = "1233";
    public string ID = "";
}

