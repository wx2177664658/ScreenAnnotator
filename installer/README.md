; installer/README.md — 安装包构建说明（CR-009）

## 技术选型

**Inno Setup 6**：中文向导成熟、可卸载、AppId 稳定升级覆盖；产出单文件 `.exe` 安装程序。

- 安装范围：每用户（`PrivilegesRequired=lowest`）
- 默认目录：`%LocalAppData%\Programs\ScreenAnnotator\`
- 内容：与 `dotnet publish -r win-x64 --self-contained` 的 `publish/ScreenAnnotator` 一致
- 卸载：不删除 `%APPDATA%\ScreenAnnotator\`

## 构建

1. 安装 [Inno Setup 6](https://jrsoftware.org/isinfo.php)
2. 仓库已附带简体中文语言文件 `ChineseSimplified.isl`（Inno 官方发行包未含）
3. 运行 `installer\build-setup.bat`
4. 产物：`dist\ScreenAnnotator-Setup-1.0.0.exe`
