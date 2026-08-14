#!/usr/bin/env python3
"""
Dify HTTP Tool 自动配置脚本
自动导入 BiddingCollector 工具到 Dify

使用方法：
1. 确保 Dify 运行在 http://127.0.0.1:18080
2. 在浏览器中登录 Dify，打开开发者工具 (F12)
3. Network 标签 → 找到任意 /console/api/ 请求 → 复制 Cookie 值
4. 设置 `DIFY_CONSOLE_COOKIE` 与 `BIDDING_API_KEY` 环境变量后运行本脚本
"""

import argparse
import json
import os
from pathlib import Path
import requests
import yaml

def load_openapi_spec():
    """加载 OpenAPI 配置"""
    spec_path = Path(__file__).with_name('dify-openapi-spec.yaml')
    with spec_path.open('r', encoding='utf-8') as f:
        return yaml.safe_load(f)

def create_tool_provider(session, base_url, api_key):
    """创建自定义工具提供者"""
    spec = load_openapi_spec()

    # 构建 Dify 工具配置
    tool_config = {
        "schema_type": "openapi",
        "schema": yaml.dump(spec),
        "provider": "custom",
        "tool_name": "BiddingCollector",
        "credentials": {
            "auth_type": "api_key",
            "api_key_header": "X-Agent-Api-Key",
            "api_key_value": api_key
        }
    }

    # 发送创建请求
    url = f"{base_url}/console/api/workspaces/current/tool-providers"
    response = session.post(url, json=tool_config)

    if response.status_code == 200:
        print("✅ 工具创建成功！")
        return response.json()
    else:
        print(f"❌ 创建失败: {response.status_code}")
        print(f"响应: {response.text}")
        return None

def main():
    parser = argparse.ArgumentParser(description='导入 BiddingCollector 工具到 Dify')
    parser.add_argument('--cookie', help='Dify Console Cookie；优先使用 DIFY_CONSOLE_COOKIE 环境变量')
    parser.add_argument('--base-url', default='http://127.0.0.1:18080', help='Dify 基础 URL')
    parser.add_argument('--api-key', help='招标 API Key；优先使用 BIDDING_API_KEY 环境变量')

    args = parser.parse_args()
    cookie = os.getenv('DIFY_CONSOLE_COOKIE') or args.cookie
    api_key = os.getenv('BIDDING_API_KEY') or args.api_key
    if not cookie:
        parser.error('请设置 DIFY_CONSOLE_COOKIE，或传入 --cookie')
    if not api_key:
        parser.error('请设置 BIDDING_API_KEY，或传入 --api-key')

    # 创建会话
    session = requests.Session()
    session.headers.update({
        'Content-Type': 'application/json',
        'Cookie': cookie
    })

    print("正在导入工具配置...")
    result = create_tool_provider(session, args.base_url, api_key)

    if result:
        print("\n=== 导入成功 ===")
        print("工具名称: BiddingCollector")
        print("可以在 Workflow 中使用该工具了！")
        print("\n测试命令:")
        print(f"  在 Dify 工具页面点击「测试」，参数留空 {{}}")
    else:
        print("\n=== 导入失败 ===")
        print("请检查:")
        print("1. Cookie 是否有效（从浏览器开发者工具复制）")
        print("2. Dify 是否正在运行")
        print("3. 是否有权限创建自定义工具")

if __name__ == "__main__":
    main()
