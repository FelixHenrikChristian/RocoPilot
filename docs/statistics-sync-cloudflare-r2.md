# 统计数据 Cloudflare R2 同步教程

Cloudflare R2 使用 S3 兼容 API。RocoPilot 会把统计文件保存为 `RocoPilot/statistics.json`，界面里只需要填写必要的连接信息。

## 1. 创建 R2 Bucket

1. 登录 Cloudflare Dashboard。
2. 进入 R2。
3. 创建一个 Bucket，例如 `roco-pilot`。
4. 记下当前账户的 Account ID。

Cloudflare R2 免费层包含 10 GB-month 存储、每月 100 万次 Class A 请求和 1000 万次 Class B 请求。统计同步只写入一个 JSON 文件，正常使用量很低。

## 2. 创建 R2 API 令牌

1. 在 R2 页面进入“Manage R2 API tokens”。
2. 创建一个 API Token。
3. 权限选择可读写对象的权限。
4. 作用范围选择刚创建的 Bucket。
5. 复制生成的 `Access Key ID` 和 `Secret Access Key`。

`Secret Access Key` 只显示一次，不要泄露给他人。

## 3. 在 RocoPilot 填写云同步设置

1. 打开“统计”页面。
2. 点击右上角“设置”。
3. 选择“云同步设置”。
4. 同步方式选择“Cloudflare R2”。
5. 填写 Account ID、Bucket、Access Key ID 和 Secret Access Key。
6. 打开“启用同步”。
7. 点击“保存”，再点击“测试连接”。

首次使用时，如果云端还没有统计文件，可以点击“上传”，把当前本地统计写入 R2。如果云端已经有其他设备的数据，请先点击“合并”，再上传合并后的本地统计。

## 4. 自动同步行为

开启云同步后，本地统计数据发生变化会延迟约 8 秒自动上传。连续多次变化只会上传最后一次结果。

上传前会检查云端文件是否已经被其他设备更新。如果云端有新版本，自动上传会停止，并提示先合并云端数据，避免旧本地数据覆盖新云端记录。

合并云端数据需要手动点击“合并”。合并会把云端账号、赛季、异色记录和待确认异色并入本地记录，不会直接覆盖本地统计。同一账号、赛季、精灵的奇遇计数属于聚合值，合并时会保留较大的计数；如果两台设备同时给同一个精灵新增计数，建议手动核对一次。

## 5. 常见问题

### 403 或签名错误

检查 Account ID、Access Key ID 和 Secret Access Key 是否填写正确，并确认 API Token 有目标 Bucket 的对象读写权限。

### 404 或云端暂无统计数据

如果还没有上传过统计文件，这是正常状态。点击“上传”后会生成 `RocoPilot/statistics.json`。

### 换设备

在新设备上填写同一个 Cloudflare R2 配置，先点击“刷新”，确认云端存在数据，再点击“合并”。如果本地也有新记录，合并完成后再点击“上传”，把合并结果写回云端。
