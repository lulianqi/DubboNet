// See https://aka.ms/new-console-template for more information
using System.Text.Json;

Console.WriteLine("TestDemoConsole");
string json = JsonSerializer.Serialize<int>(123);
Console.WriteLine(json);
json = JsonSerializer.Serialize<string>("hi 123");
Console.WriteLine(json);
Console.ReadLine();
