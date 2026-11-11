#!/bin/sh
# Runtime config: backend URL comes from the API_URL environment variable.
echo "window.__API_URL__ = '${API_URL:-http://localhost:8080}';" > /usr/share/nginx/html/config.js
