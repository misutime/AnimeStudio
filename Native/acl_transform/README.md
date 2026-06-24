# acl_transform

`acl_transform.dll` 是 AnimeStudio 自己的轻量 ACL 解码入口，用来把 Unity/Endfield 里的 ACL `compressed_tracks` 直接采样成 TRS/float 数组。

当前用途很窄：

- `qvvf` transform track：每条 track 输出 12 个 float，顺序是 rotation xyzw、translation xyzw、scale xyzw。
- scalar track：每条 track 输出 1 个 float。
- Unity binding、骨骼路径、坐标系转换和 glTF 写出都在 C# 层完成。

依赖来源：

- 官方 ACL：<https://github.com/nfrechette/acl>
- ACL 的 RTM 子模块：`external/rtm`

重建命令示例：

```powershell
$vcvars = 'C:\Program Files\Microsoft Visual Studio\18\Community\VC\Auxiliary\Build\vcvars64.bat'
$acl = Join-Path $env:TEMP 'acl-latest'
$src = 'D:\misutime\AnimeStudio\Native\acl_transform\acl_transform.cpp'
$outDir = 'D:\misutime\AnimeStudio\AnimeStudio.Libraries\x64'

cmd /c "`"$vcvars`" && cl /nologo /utf-8 /std:c++17 /O2 /EHsc /LD `"$src`" /I`"$acl\includes`" /I`"$acl\external\rtm\includes`" /Fe`"$outDir\acl_transform.dll`" /link /NOLOGO"
```

`acl-latest` 应是带 submodule 的官方 ACL checkout。不要改用旧版 `acl.dll` 或 npm `@nfrechette/acl` 1.x 结果来判断 Endfield 样本是否有效；实测 Endfield 的 ACL v10 transform buffer 需要新版官方 ACL 头文件才能稳定解码。

C# `byte[]` 传入 native 时地址不保证 16 字节对齐；ACL validation 对 `compressed_tracks` 对齐很敏感。DLL 入口会先把输入复制到 16 字节对齐的临时缓冲区，再调用 ACL。不要删掉这层复制，否则部分 Endfield clip 会在 .NET 侧返回 `-2`，但同一 buffer 在偶然对齐的 native/Python 测试里又看起来正常。
