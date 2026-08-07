# 网络问题记录：WSL 中 Docker Hub 无法连接

## 记录信息

- **记录日期:** 2026-08-04
- **环境:** Windows + WSL2 + Ubuntu 24.04
- **适用场景:** Docker Engine 已安装，但拉取 Docker Hub 镜像时出现连接超时或连接被拒绝
- **状态:** 已验证解决

## 问题现象

Docker Engine、Docker Compose 和清华 Docker CE 软件源均正常，但执行以下测试时无法访问 Docker Hub：

```bash
docker run --rm hello-world
```

典型错误：

```text
failed to resolve reference "docker.io/library/hello-world:latest"
connect: connection refused
```

或：

```text
Failed to connect to registry-1.docker.io port 443
Connection timed out
```

## 诊断结论

本次不是 WSL 整体断网：

- Ubuntu 软件源和清华 Docker CE 软件源可以访问；
- Docker Hub 的 `registry-1.docker.io:443` 直连超时或被拒绝；
- Docker 服务本身正常，问题发生在镜像仓库连接路径；
- 因此优先配置 Docker Registry Mirror，而不是重复安装 Docker 或修改 apt 软件源。

可复用的诊断命令：

```bash
getent ahostsv4 registry-1.docker.io
curl -4 -I --connect-timeout 10 --max-time 20 https://registry-1.docker.io/v2/
env | grep -iE '^(http|https|all|no)_proxy=' || true
systemctl show docker --property=Environment --no-pager
docker info --format 'Server={{.ServerVersion}} Proxy={{json .HTTPProxy}} NoProxy={{json .NoProxy}}'
```

## 已验证的解决方式

本次使用可连通的 Docker 镜像加速地址：

```text
https://docker.m.daocloud.io
```

以 root 身份写入 Docker daemon 配置：

```bash
sudo mkdir -p /etc/docker
sudo tee /etc/docker/daemon.json >/dev/null <<'EOF'
{"registry-mirrors":["https://docker.m.daocloud.io"]}
EOF
sudo systemctl restart docker
```

确认配置已被 Docker 读取：

```bash
docker info --format '{{json .RegistryConfig.Mirrors}}'
```

预期能看到：

```text
["https://docker.m.daocloud.io/"]
```

然后执行完整验证：

```bash
docker pull hello-world
docker run --rm hello-world
docker run --rm ubuntu:24.04 echo container-ok
docker compose version
```

本次已验证结果：

- `hello-world` 成功拉取并运行；
- `ubuntu:24.04` 成功拉取并运行；
- Docker Compose 成功读取临时 Compose 文件并运行服务；
- 普通用户 `slience996` 无需 `sudo` 即可访问 Docker Engine。

## 后续复用规则

1. 先执行“诊断命令”，确认是 Docker Hub 连接问题，而不是 Docker 服务未启动。
2. 检查 `/etc/docker/daemon.json` 是否已有组织内部或云厂商镜像配置；已有配置时先备份，不要直接覆盖。
3. 公共镜像加速服务可能发生域名、策略或可用性变化；本记录中的地址是本次已验证地址，不保证永久可用。
4. 生产环境优先使用组织自建 Registry、云厂商为当前账号提供的专属镜像加速器，或公司代理；不要把公共镜像服务视为唯一生产依赖。
5. 修改 daemon 配置后必须执行 `systemctl restart docker`，再用 `docker info` 和 `docker run --rm hello-world` 验证。
6. 不要在该文件中记录密码、访问令牌、私有仓库凭据或代理认证信息。

## 未采用的方案

本次没有修改 WSL DNS、关闭安全策略、重复安装 Docker，也没有把 apt 的 `Acquire::ForceIPv4` 设置误当成 Docker daemon 的网络配置。apt 能联网并不代表 Docker Hub 直连一定可用。
## 另一类已验证问题：WSL 无法直连 GitHub

### 现象

WSL 中执行官方仓库克隆时：

`	ext
fatal: unable to access 'https://github.com/langgenius/dify.git/':
Failed to connect to github.com port 443
`

但同一台 Windows 主机可以访问 GitHub。

### 已验证的安全处理方式

不要使用不明 GitHub 代理或第三方安装包。使用 GitHub 官方 API 获取目标 Commit，再从 GitHub 官方 codeload 下载对应源码归档：

`powershell
$meta = Invoke-RestMethod 
  -Uri 'https://api.github.com/repos/langgenius/dify/commits/main' 
  -Headers @{ 'User-Agent' = 'market-intelligence-agent-setup' }
$sha = $meta.sha
Invoke-WebRequest 
  -Uri "https://codeload.github.com/langgenius/dify/tar.gz/$sha" 
  -OutFile '.tmp-dify-main.tar.gz'
`

然后把归档解压到 WSL 的 ~/services/dify，并记录下载时的 Commit SHA。这样仍然使用官方 GitHub 源，且部署版本可追溯。

### 复用注意事项

- 先测试 Windows 能否访问 GitHub，再判断是否只是 WSL 网络路径问题；
- 记录完整 Commit SHA，不要只记录 main；
- 临时归档部署完成后应删除；
- 不要把 Dify 源码归档放进本项目并提交；
- 如果 Windows 也无法访问 GitHub，应先处理主机代理/网络，不要贸然使用未知代理。

## 另一类已验证问题：Compose 并行拉取镜像 TLS 超时

即使已配置镜像加速，Dify Compose 一次并行拉取大量镜像时，仍可能出现：

`	ext
net/http: TLS handshake timeout
`

本次采用的解决方式是先用 docker compose config --images 列出镜像，然后用普通 docker pull IMAGE 逐个拉取；全部镜像完成后再执行：

`ash
docker compose up -d
`

逐个拉取比直接并行执行 docker compose up -d 更稳定。若公共镜像加速器后续不可用，应替换为组织内部或云厂商专属 Registry 加速器。
