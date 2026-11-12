#!/bin/bash
# Sourced by install_service.sh after curl, jq and zstd are installed.
# Logging helpers are supplied by the installer. All requests target the same
# configured daemon; the CLI's inherited OLLAMA_HOST cannot select another one.

ollama_install_latest() {
    local installer result=0
    installer=$(mktemp)
    log_info "Installing/upgrading Ollama using the official Linux installer..."
    if curl --fail --show-error --silent --location --proto '=https' --tlsv1.2 \
        https://ollama.com/install.sh -o "$installer" && sh -n "$installer"; then
        sh "$installer" || result=$?
    else
        result=1
    fi
    rm -f "$installer"
    if [ "$result" -ne 0 ]; then
        log_error "Ollama installation failed. Check network access and sudo permissions."
        return "$result"
    fi
}

ollama_start_local() {
    local settings attempt
    settings=$(mktemp)
    cat > "$settings" <<EOF
[Service]
Environment="OLLAMA_HOST=$OLLAMA_BIND"
Environment="OLLAMA_NUM_PARALLEL=1"
Environment="OLLAMA_MAX_LOADED_MODELS=1"
EOF
    if ! sudo install -d -m 755 /etc/systemd/system/ollama.service.d ||
        ! sudo install -m 644 "$settings" /etc/systemd/system/ollama.service.d/openscanner.conf; then
        rm -f "$settings"
        return 1
    fi
    rm -f "$settings"
    sudo systemctl daemon-reload || return 1
    sudo systemctl enable ollama || return 1
    sudo systemctl restart ollama || return 1
    for ((attempt=1; attempt<=20; attempt++)); do
        if curl --fail --silent --show-error --connect-timeout 2 --max-time 2 \
            "$OLLAMA_URL/api/version" >/dev/null 2>&1; then
            return 0
        fi
        if (( attempt % 5 == 0 )); then log_info "Waiting for Ollama ($attempt/20)..."; fi
        sleep 1
    done
    log_error "Ollama did not become ready. Check: journalctl -u ollama -n 50"
    return 1
}

ollama_api() {
    local path=$1 payload=$2 timeout=$3 response
    response=$(mktemp)
    OLLAMA_HTTP_BODY=""
    OLLAMA_HTTP_STATUS="000"
    if ! OLLAMA_HTTP_STATUS=$(curl --silent --show-error --connect-timeout 5 --max-time "$timeout" \
        -o "$response" -w '%{http_code}' -H 'Content-Type: application/json' \
        --data-binary "$payload" "$OLLAMA_URL/api/$path"); then
        rm -f "$response"
        log_error "Could not contact Ollama ($path). Check Address.OllamaUrl, network access and service logs."
        return 1
    fi
    OLLAMA_HTTP_BODY=$(cat "$response")
    rm -f "$response"
}

ollama_needs_upgrade() {
    [ "$OLLAMA_HTTP_STATUS" = "412" ] ||
        [[ "$OLLAMA_HTTP_BODY" =~ [Nn]ewer\ version\ of\ Ollama|[Uu]pgrade.*Ollama|Ollama.*[Uu]pgrade ]]
}

ollama_report_error() {
    local detail
    detail=$(jq -r '.error // "Unexpected Ollama response"' <<< "$OLLAMA_HTTP_BODY" 2>/dev/null) || detail="Invalid Ollama response"
    log_error "Ollama HTTP $OLLAMA_HTTP_STATUS: ${detail:0:1000}"
}

setup_ollama() {
    local appsettings=$1 requested_model=$2 requested_url=$3 force_upgrade=$4
    local current_model choice model_payload pull_payload verify_payload upgraded=false
    OLLAMA_URL=${requested_url:-${Address__OllamaUrl:-$(jq -r '.Address.OllamaUrl // "http://127.0.0.1:11434/"' "$appsettings")}}
    current_model=${Address__Model:-$(jq -r '.Address.Model // "qwen3:4b"' "$appsettings")}
    LLM_MODEL=$requested_model
    if [ -z "$LLM_MODEL" ]; then
        echo ""
        log_info "LLM for transmission metadata and address extraction (separate from Whisper):"
        echo "  1) qwen3:4b   — default, approximately 2.5 GB download"
        echo "  2) qwen3:1.7b — smaller option for limited memory"
        echo "  3) Custom model (must support structured JSON and think=false)"
        read -r -p "Select [1-3], or Enter to keep $current_model: " choice
        case "$choice" in
            1) LLM_MODEL="qwen3:4b" ;;
            2) LLM_MODEL="qwen3:1.7b" ;;
            3) read -r -p "Ollama model name: " LLM_MODEL ;;
            "") LLM_MODEL="$current_model" ;;
            *) log_error "Invalid LLM selection."; return 1 ;;
        esac
    fi
    if ! [[ "$LLM_MODEL" =~ ^[a-zA-Z0-9][a-zA-Z0-9._/:@-]{0,199}$ ]]; then
        log_error "Invalid Ollama model name. Use a name such as qwen3:4b."
        return 1
    fi
    # The application uses /api/chat at the origin root; paths/auth/query strings
    # in the configured URL would not describe the same endpoint.
    if ! [[ "$OLLAMA_URL" =~ ^https?://(\[[0-9a-fA-F:]+\]|[a-zA-Z0-9._-]+)(:[0-9]{1,5})?/?$ ]]; then
        log_error "Ollama URL must be an HTTP(S) origin, such as http://127.0.0.1:11434/."
        return 1
    fi
    OLLAMA_URL=${OLLAMA_URL%/}
    OLLAMA_MANAGED_LOCAL=false
    if [[ "$OLLAMA_URL" =~ ^http://(localhost|127\.0\.0\.1|\[::1\]):([0-9]+)$ ]]; then
        local host=${BASH_REMATCH[1]} port=${BASH_REMATCH[2]}
        if (( 10#$port < 1024 || 10#$port > 65535 )); then
            log_error "Managed local Ollama requires a loopback port from 1024 to 65535."
            return 1
        fi
        [ "$host" = "localhost" ] && host="127.0.0.1"
        OLLAMA_BIND="$host:$port"
        OLLAMA_MANAGED_LOCAL=true
        if [ "$force_upgrade" = true ] || ! command -v ollama >/dev/null 2>&1 ||
            ! systemctl cat ollama.service >/dev/null 2>&1; then
            ollama_install_latest || return 1
            upgraded=true
        fi
        ollama_start_local || return 1
    else
        if [ "$force_upgrade" = true ]; then
            log_error "--upgrade-ollama applies only to a managed local loopback service."
            return 1
        fi
        log_info "Using an externally managed Ollama endpoint; local installation and service settings are unchanged."
    fi

    model_payload=$(jq -nc --arg model "$LLM_MODEL" '{model:$model}')
    pull_payload=$(jq -nc --arg model "$LLM_MODEL" '{model:$model,stream:false}')
    while true; do
        ollama_api show "$model_payload" 30 || return 1
        if [ "$OLLAMA_HTTP_STATUS" = "200" ] && jq -e 'type == "object" and (has("error") | not)' <<< "$OLLAMA_HTTP_BODY" >/dev/null; then
            log_info "Ollama model already installed: $LLM_MODEL"
            break
        fi
        if [ "$OLLAMA_HTTP_STATUS" = "404" ]; then
            log_info "Downloading Ollama model $LLM_MODEL (first download may take several minutes)..."
            ollama_api pull "$pull_payload" 1800 || return 1
            if [ "$OLLAMA_HTTP_STATUS" = "200" ] && jq -e '.status == "success" and (has("error") | not)' <<< "$OLLAMA_HTTP_BODY" >/dev/null; then
                break
            fi
        fi
        if ollama_needs_upgrade && [ "$OLLAMA_MANAGED_LOCAL" = true ] && [ "$upgraded" = false ]; then
            log_info "The selected model requires a newer Ollama; upgrading and retrying..."
            ollama_install_latest || return 1
            ollama_start_local || return 1
            upgraded=true
        else
            ollama_report_error
            log_error "Model setup failed. For an old local version use --upgrade-ollama; upgrade remote servers on their host."
            return 1
        fi
    done

    log_info "Verifying model loading and structured JSON (model will unload afterward)..."
    verify_payload=$(jq -nc --arg model "$LLM_MODEL" '{model:$model,stream:false,think:false,keep_alive:0,
        format:{type:"object",properties:{ready:{type:"boolean",enum:[true]}},required:["ready"],additionalProperties:false},
        options:{temperature:0,num_ctx:4096,num_predict:64},
        messages:[{role:"system",content:"Return only JSON with ready set to true."},{role:"user",content:"Installation check."}]}')
    ollama_api chat "$verify_payload" 180 || return 1
    if [ "$OLLAMA_HTTP_STATUS" != "200" ] || ! jq -e '
        .done == true and .done_reason != "length" and
        ((.message.content | fromjson) == {ready:true})' <<< "$OLLAMA_HTTP_BODY" >/dev/null 2>&1; then
        ollama_report_error
        log_error "LLM verification failed. Check model compatibility, memory, and Ollama logs. Configuration was not saved."
        return 1
    fi
    local updated
    updated=$(jq --arg model "$LLM_MODEL" --arg url "$OLLAMA_URL/" \
        '.Address.Model=$model | .Address.OllamaUrl=$url' "$appsettings") || return 1
    printf '%s\n' "$updated" > "$appsettings"
    log_success "Ollama model ready and verified: $LLM_MODEL"
}
