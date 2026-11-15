# misaki-spike

MisakiSharp 穿刺探针，**仅本地验证、不随应用发布**。用于确认能否从管线中间层取到界面注音数据（中文原始拼音、日语振假名、英语逐词 IPA）。

- 用途与结论见 [../../notes/misaki-spike.md](../../notes/misaki-spike.md)。
- 运行：
  - `dotnet run -c Debug`
  - `dotnet publish -c Release -r win-x64 -p:Aot=true` 后跑 `bin/Release/net11.0/win-x64/publish/MisakiSpike.exe`
- 该目录不属于 `src/`，不参与主程序构建。
