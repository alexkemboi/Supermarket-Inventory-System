#!/usr/bin/env bash
# One-shot (and re-runnable) deploy for Ubuntu 24.04.
#   Frontend: https://superinventory.alexkemboi.dev -> web container (127.0.0.1:8090)
#   API:      https://apiinventory.alexkemboi.dev   -> api container (127.0.0.1:5080)
#   DB:       SQL Server container, not exposed outside Docker
# Usage (as root, from the repo root):  bash deploy/server-setup.sh
set -euo pipefail

WEB_DOMAIN="${WEB_DOMAIN:-superinventory.alexkemboi.dev}"
API_DOMAIN="${API_DOMAIN:-apiinventory.alexkemboi.dev}"
CERT_EMAIL="${CERT_EMAIL:-info@ikonexsystems.com}"
APP_DIR="$(cd "$(dirname "$0")/.." && pwd)"
cd "$APP_DIR"

echo "==> Installing packages"
if ! command -v docker >/dev/null; then
  curl -fsSL https://get.docker.com | sh
fi
apt-get update -y
apt-get install -y nginx certbot python3-certbot-nginx

echo "==> Writing .env"
if [ ! -f .env ]; then
  # SQL Server needs upper + lower + digit; keep it free of ; and quotes for the connection string.
  PASS="Inv$(openssl rand -hex 12)A9z"
  cat > .env <<EOF
MSSQL_SA_PASSWORD=$PASS
CORS_ALLOWED_ORIGINS=https://$WEB_DOMAIN
SEED_ORDER_COUNT=250000
EOF
  chmod 600 .env
fi

echo "==> Building and starting containers"
docker compose -f docker-compose.prod.yml up -d --build

echo "==> Configuring host nginx"
cat > /etc/nginx/sites-available/inventory.conf <<EOF
map \$http_upgrade \$connection_upgrade {
    default upgrade;
    ''      close;
}

server {
    listen 80;
    server_name $WEB_DOMAIN;
    client_max_body_size 10m;

    location / {
        proxy_pass http://127.0.0.1:8090;
        proxy_http_version 1.1;
        proxy_set_header Upgrade \$http_upgrade;
        proxy_set_header Connection \$connection_upgrade;
        proxy_set_header Host \$host;
        proxy_set_header X-Forwarded-For \$proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto \$scheme;
        proxy_read_timeout 1h;
    }
}

server {
    listen 80;
    server_name $API_DOMAIN;
    client_max_body_size 10m;

    location / {
        proxy_pass http://127.0.0.1:5080;
        proxy_http_version 1.1;
        proxy_set_header Upgrade \$http_upgrade;
        proxy_set_header Connection \$connection_upgrade;
        proxy_set_header Host \$host;
        proxy_set_header X-Forwarded-For \$proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto \$scheme;
        proxy_read_timeout 1h;
    }
}
EOF
ln -sf /etc/nginx/sites-available/inventory.conf /etc/nginx/sites-enabled/inventory.conf
nginx -t
systemctl reload nginx

echo "==> Issuing TLS certificates"
if command -v ufw >/dev/null && ufw status | grep -q "Status: active"; then
  ufw allow 'Nginx Full'
fi
certbot --nginx --non-interactive --agree-tos --redirect -m "$CERT_EMAIL" -d "$WEB_DOMAIN" -d "$API_DOMAIN"

echo "==> Waiting for API health (first start seeds the database and can take a few minutes)"
for i in $(seq 1 90); do
  if curl -fsS http://127.0.0.1:5080/health >/dev/null 2>&1; then
    echo "API healthy"; break
  fi
  sleep 5
done

docker compose -f docker-compose.prod.yml ps
echo
echo "Frontend: https://$WEB_DOMAIN"
echo "API:      https://$API_DOMAIN/health"
