# 第三方组件与权利声明

本仓库及其发布包（插件 zip）包含或依赖以下第三方作品。它们的权利归各自所有者，  
本项目仅在其许可条款下使用。**本文件本身不受 `LICENSE` 的许可条款约束。**

---

## 1. 中文字体

**文件**：`data/fonts/font.ttf` ｜ **随发布包分发**

- **字体**：Source Han Sans CN（思源黑体 CN）Version 2.000
- **版权**：© 2014, 2015, 2018 Adobe (<http://www.adobe.com/>), with Reserved Font Name 'Source'.
- **许可**：SIL Open Font License, Version 1.1
- **许可全文**：<https://scripts.sil.org/OFL>

字体以「AS IS」基础分发，不附带任何明示或暗示的担保。许可信息同时内嵌于字体文件  
自身的元数据（`name` 表）中，可用任意字体工具查看。

> OFL 允许本字体被收录进更大的作品并随其分发，且不要求该作品采用同样的许可。  
> 因此本项目的 MIT 许可（见 [`LICENSE`](LICENSE)）不影响字体本身的授权条件，  
> 也不对字体施加额外限制。

## 2. JSON 解析库

**文件**：`Newtonsoft.Json.dll` ｜ **随发布包分发**

- **项目**：Json.NET (Newtonsoft.Json) 13.0.4
- **版权**：Copyright (c) 2007 James Newton-King
- **许可**：MIT License
- **项目地址**：<https://github.com/JamesNK/Newtonsoft.Json>

```
MIT License

Copyright (c) 2007 James Newton-King

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

## 3. 运行时框架（未随本仓库分发）

- **BepInEx 5** —— 许可 LGPL-2.1。本插件以 BepInEx 插件形式运行，但**不包含**  
  BepInEx 的任何文件；BepInEx 由使用者自行安装。  
  <https://github.com/BepInEx/BepInEx>

## 4. 游戏内容

- **Nuclear Option** 及其全部游戏文本、名称、素材的版权归其开发商所有。
- `data/` 下的词表以游戏原文作为**键**（用于匹配待翻译的界面文本），译文由本项目译者撰写。  
  游戏原文本身不在本项目的可授权范围内。
- 本项目与 Nuclear Option 的开发方无任何关联，亦未获其授权或背书。

## 5. Unity 引擎程序集（未随本仓库分发）

`src/` 在编译时引用游戏自带的 `UnityEngine*.dll`，仅作编译期引用  
（csproj 中标记 `Private=false`），**不会**被复制进发布包，也不会随本仓库分发。

## 6. 派生关系声明

本项目的词表（`data/translation.json`）中有少量条目源自或参考了**前身项目**：

- **前身项目**：[HunterCHCL/NuclearOption-Chinese-Translation-mod](https://github.com/HunterCHCL/NuclearOption-Chinese-Translation-mod)
- **可追溯条目**：指译文与上游提供的翻译素材逐字一致的条目。另有少数条目在更早的开发阶段参考过上游素材，其后已作改写。
- **上游许可状态**：⚠️ **上游未附任何许可证文件**，因此**未明确授予**复制、修改、再分发的权利。
- **本项目立场**：对上述可追溯的部分，**本项目不主张著作权**，其权利归上游作者所有。  
  其余的译文，以及 `src/` 下的全部代码，均为本项目作者自行撰写 / 实现。
- **联系**：若原作者对收录方式有异议，请通过本仓库的 issue 联系我们，我们会依其意愿调整或移除。

> `src/` 下的插件代码为本项目**从零重写**的原创实现，未沿用上游源码；但插件框架、  
> 翻译流水线、字体回退机制等**设计思路**受上游启发。设计思路本身不受著作权保护，  
> 此处声明仅为如实说明来源。
>
> ⚠️ 需要注意：**上游没有许可，不等于本项目"继承"了上游的授权状态**。本声明只是如实披露  
> 派生关系。若你打算把本项目用于需要权利链条完全清晰的场合（例如商业分发），  
> 请自行评估带来的不确定性。
