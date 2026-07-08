# Build the React app
FROM node:22-alpine AS build
WORKDIR /app
COPY package*.json ./
RUN npm ci
COPY . .
# Entra/API settings are baked at build time via VITE_* build args.
ARG VITE_ENTRA_TENANT_ID
ARG VITE_ENTRA_CLIENT_ID
ARG VITE_API_SCOPE
ARG VITE_API_BASE_URL
RUN npm run build

# Serve with nginx; API_UPSTREAM is substituted into the config at container start.
FROM nginx:1.27-alpine AS final
ENV API_UPSTREAM=http://dashboard-api:8080
COPY nginx.conf.template /etc/nginx/templates/default.conf.template
COPY --from=build /app/dist /usr/share/nginx/html
EXPOSE 8080
