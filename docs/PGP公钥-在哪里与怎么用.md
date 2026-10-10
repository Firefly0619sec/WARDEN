# WARDEN 的 PGP 公钥 —— 在哪里、怎么用

> 面向：所有用户（尤其想私密反馈问题的人）
> 维护者：Firefly0619sec
> 更新：2026-10-10

---

## 一、先说结论：公钥是公开的，泄露出去没有风险

```
★ 公钥本来就是设计成【可以公开】的 ✓
★ 别人拿到它只能做一件事：给你的内容【加密】
   —— 而加密之后的东西，只有你的私钥能解开
   —— 攻击者自己都解不开他自己加密的文件 ✗
★ 所以：把它贴在 README、写在安装目录、发到群里，都没问题 ✓
```

**★★ 反过来说，公钥还有个好处：它给了用户一条【能加密的联系你的方式】✓**
```
用户想反馈问题，但内容涉及隐私（比如日志里有自己的工作路径、聊天窗口标题）
  -> 用你的公钥加密后再提交
  -> 即使贴在公开的 GitHub issue 上，别人也看不懂 ✓
  -> 只有你能解开 ✓
```

---

## 二、公钥在哪里

| 位置 | 说明 |
| --- | --- |
| **安装目录** `keys\PGP-PUBLIC-KEY.asc` | 随安装包装好，本地就能用 ✓ |
| **仓库** `keys/PGP-PUBLIC-KEY.asc` | 源码仓库里也有一份 |
| **程序内** | 已内嵌进 WARDEN.exe（用来校验停止文件签名） |

**★ 指纹（用来确认是不是同一把钥匙）：**
```
EBFB6B895D8EEC8F59CE0A26E23876CBBDAE330F
```

**★ 短 ID（日常够用）：**
```
E23876CBBDAE330F
```

**★ 如果不一致，就是假钥匙 ✗ 一定要比对指纹。**

---

## 三、怎么用（三种场景）

### 场景 1：给维护者发一条加密消息

```bash
# ① 导入公钥
gpg --import keys/PGP-PUBLIC-KEY.asc

# ② 确认指纹
gpg --fingerprint E23876CBBDAE330F
#    应该显示 EBFB6B895D8EEC8F59CE0A26E23876CBBDAE330F

# ③ 写你的消息
notepad 我的反馈.txt

# ④ 加密（输出成 ASCII 文本，能直接粘贴）
gpg --armor --recipient E23876CBBDAE330F --encrypt 我的反馈.txt
#    -> 生成 我的反馈.txt.asc

# ⑤ 把 我的反馈.txt.asc 里的内容【整段】粘进 GitHub issue 或发给我
```

★ **不用 gpg 也行**：WARDEN 的「反馈问题」页会调 gpg 帮你加密（待接线，见任务 A56）。

### 场景 2：验证我发给你的东西是不是我发的

```bash
# 我发布的东西会带 .asc 签名，例如 WARDEN-Setup.exe.asc
gpg --verify WARDEN-Setup.exe.asc WARDEN-Setup.exe
#   看到 "Good signature" 且指纹是 EBFB6B89... 就对了 ✓
```

### 场景 3：自己关掉 WARDEN（高级用法）

WARDEN 接受的"关闭请求"必须带你的签名：

```bash
# ① 写内容
echo stop > %LOCALAPPDATA%\WARDEN\WARDEN.stop

# ② 分离签名
gpg --armor --detach-sign --local-user E23876CBBDAE330F ^
    --output %LOCALAPPDATA%\WARDEN\WARDEN.stop.asc ^
    %LOCALAPPDATA%\WARDEN\WARDEN.stop

# ③ WARDEN 会在下一次轮询时验证签名并退出 ✓
```

**★★ 这正是"只有主人能关"的机制：**
```
★ 流氓软件没有你的私钥 -> 签不出来 -> 关不掉 WARDEN ✓
★ 光写个 WARDEN.stop 文件没用（会被当成攻击，记日志 + 告警）✓
```

---

## 四、为什么不用对称密钥（我踩过的坑，记一下）

```
初版设计：随机密钥 + DPAPI 保护 + HMAC 校验
  ✗ 密钥存在同一台机器上、同一个用户下
  ✗ 同一个用户跑的任何程序都能调 DPAPI 把密钥解出来
  -> 流氓软件照样能伪造关闭请求 ✗

★ 改成 PGP 之后：私钥永远不离开主人 ✓
  -> 攻击者拿到公钥也签不出来 ✓✓✓
```

---

## 五、给维护者自己的备忘

- 私钥**永远不要**放进仓库、安装包、任何会被分发的地方 ✓
- 备份私钥（离线、加密）：丢了就再也解不开别人发来的加密反馈 ✗
- 撤销证书：建议生成一份放着，万一私钥泄漏可以宣告作废
- 发布时给安装包加 `.asc` 分离签名，用户就能验证：
  ```bash
  gpg --armor --detach-sign --output WARDEN-Setup.exe.asc WARDEN-Setup.exe
  ```
