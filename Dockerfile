FROM golang:1.26-alpine AS builder
RUN apk add --no-cache gcc musl-dev linux-headers
WORKDIR /src
COPY go.mod go.sum ./
RUN go mod download
COPY . .
ARG PKG=./clients/cli
RUN CGO_ENABLED=0 go build -o /app ${PKG}

FROM alpine:3.21
RUN apk add --no-cache iptables iproute2 iperf3
COPY --from=builder /app /app
ENTRYPOINT ["/app"]
