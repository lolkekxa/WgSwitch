#!/usr/bin/env bash
# WgSwitch · сервер — туннели WireGuard / AmneziaWG и устройства одной командой.
#
#   Установка:  curl -fsSL https://raw.githubusercontent.com/lolkekxa/WgSwitch/main/server/wgswitch-server.sh \
#                 -o /usr/local/bin/wgswitch-server && chmod +x /usr/local/bin/wgswitch-server && wgswitch-server
#   Потом:      sudo wgswitch-server
#
# Правила, на которых сломаться нельзя:
#   * ключ сервера — строкой PrivateKey в конфиге (не через PostUp): иначе `syncconf` оставит сервер без ключа;
#   * устройства добавляются и удаляются через `wg set` + правку файла — работающие туннели не трогаются;
#   * приватные ключи на экран не выводятся: устройству — QR или файл.
set -euo pipefail

ETC=${WGS_ETC:-/etc}                 # для тестов: подменить корень
STATE=$ETC/wgswitch
CLIENTS=$STATE/clients
NAME_RE='^[a-zA-Z0-9_=+.-]{1,15}$'

if [ -t 1 ]; then G=$'\e[32m' Y=$'\e[33m' R=$'\e[31m' D=$'\e[2m' B=$'\e[1m' N=$'\e[0m'; else G= Y= R= D= B= N=; fi
ok()   { printf '%s✓%s %s\n' "$G" "$N" "$*"; }
warn() { printf '%s!%s %s\n' "$Y" "$N" "$*"; }
die()  { printf '%s✗ %s%s\n' "$R" "$*" "$N" >&2; exit 1; }
hint() { printf '%s%s%s\n' "$D" "$*" "$N"; }

# вопросы читаются с терминала, даже если скрипт запущен через конвейер
TTY=${WGS_TTY:-/dev/tty}
ask() {
    local q=$1 def=${2-} a=''
    if [ -n "$def" ]; then printf '%s %s[%s]%s: ' "$q" "$D" "$def" "$N"; else printf '%s: ' "$q"; fi
    IFS= read -r a <&3 || true
    REPLY=${a:-$def}
}
yes_no() { ask "$1 (y/n)" "${2:-n}"; [[ $REPLY =~ ^[YyДд] ]]; }

# ---------- движок по имени интерфейса: awg* — AmneziaWG, остальное — WireGuard ----------

is_awg()    { [[ $1 == awg* ]]; }
tool()      { if is_awg "$1"; then echo awg; else echo wg; fi; }
quick()     { if is_awg "$1"; then echo awg-quick; else echo wg-quick; fi; }
conf_path() { if is_awg "$1"; then echo "$ETC/amnezia/amneziawg/$1.conf"; else echo "$ETC/wireguard/$1.conf"; fi; }
service()   { echo "$(quick "$1")@$1"; }
engine()    { if is_awg "$1"; then echo AmneziaWG; else echo WireGuard; fi; }

interfaces() {
    local f
    for f in "$ETC"/wireguard/*.conf "$ETC"/amnezia/amneziawg/*.conf; do
        [ -f "$f" ] || continue
        grep -qi '^[[:space:]]*\[Interface\]' "$f" && basename "$f" .conf
    done
}

# значение ключа из секции [Interface]
iface_get() {
    awk -v k="$(printf '%s' "$2" | tr 'A-Z' 'a-z')" '
        /^[ \t]*\[/ { s = tolower($0); next }
        s ~ /interface/ && index($0, "=") {
            key = substr($0, 1, index($0, "=") - 1); gsub(/[ \t]/, "", key)
            if (tolower(key) == k) { v = substr($0, index($0, "=") + 1); gsub(/^[ \t]+|[ \t]+$/, "", v); print v; exit }
        }' "$(conf_path "$1")"
}

# адрес сервера в туннеле: 10.8.0.1 из «Address = 10.8.0.1/24»
server_ip() { iface_get "$1" Address | tr ',' '\n' | tr -d ' ' | grep -m1 -E '^[0-9]+\.' | cut -d/ -f1; }

used_ips() {
    grep -iE '^[[:space:]]*(AllowedIPs|Address)[[:space:]]*=' "$(conf_path "$1")" \
        | sed 's/^[^=]*=//' | tr ',' '\n' | tr -d ' \t' | cut -d/ -f1 | grep -E '^[0-9.]+$' || true
}

next_ip() {
    local base used n
    base=$(server_ip "$1"); base=${base%.*}
    used=$(used_ips "$1")
    for n in $(seq 2 254); do
        grep -qx "$base.$n" <<<"$used" || { echo "$base.$n"; return; }
    done
    die "В подсети $base.0/24 не осталось свободных адресов"
}

# параметры маскировки AmneziaWG — у устройства должны быть точно такие же, как у сервера
awg_params() {
    awk '/^[ \t]*\[/ { s = tolower($0); next }
         s ~ /interface/ && /^[ \t]*(Jc|Jmin|Jmax|S[1-4]|H[1-4]|I[1-5])[ \t]*=/ { sub(/^[ \t]+/, ""); print }' "$(conf_path "$1")"
}

server_pub() { iface_get "$1" PrivateKey | "$(tool "$1")" pubkey; }

endpoint() {
    local f="$STATE/$1.endpoint" host
    if [ -s "$f" ]; then cat "$f"; return; fi
    host=$(curl -4 -fsS --max-time 5 https://api.ipify.org 2>/dev/null || true)
    [ -n "$host" ] || host=$(ip -4 route get 1.1.1.1 2>/dev/null | awk '{for (i = 1; i < NF; i++) if ($i == "src") { print $(i + 1); exit }}')
    ask "Адрес сервера для устройств (IP или домен)" "$host" >&2
    mkdir -p "$STATE"; printf '%s\n' "$REPLY" >"$f"
    echo "$REPLY"
}

need_root() { [ "${WGS_NO_ROOT:-}" = 1 ] || [ "$(id -u)" = 0 ] || die "Запустите от root: sudo wgswitch-server"; }

pick_iface() {
    local list; mapfile -t list < <(interfaces)
    [ ${#list[@]} -gt 0 ] || die "Туннелей ещё нет — выберите «Новый туннель»"
    if [ ${#list[@]} -eq 1 ]; then echo "${list[0]}"; return; fi
    local i
    for i in "${!list[@]}"; do
        printf '  %s) %s — %s, порт %s\n' $((i + 1)) "${list[$i]}" "$(engine "${list[$i]}")" "$(iface_get "${list[$i]}" ListenPort)" >&2
    done
    ask "Какой туннель" 1 >&2
    [[ $REPLY =~ ^[0-9]+$ ]] && [ "$REPLY" -ge 1 ] && [ "$REPLY" -le ${#list[@]} ] || die "Нет такого номера"
    echo "${list[$((REPLY - 1))]}"
}

# ---------- старая ловушка: ключ через PostUp ----------

fix_postup_key() {
    local f; f=$(conf_path "$1")
    grep -qiE '^[[:space:]]*PrivateKey[[:space:]]*=' "$f" && return 0
    local line kf
    line=$(grep -iE '^[[:space:]]*PostUp[[:space:]]*=.*private-key' "$f" | head -1 || true)
    [ -n "$line" ] || die "В $f нет ключа сервера (PrivateKey)"
    if ! [[ $line =~ ^[[:space:]]*PostUp[[:space:]]*=[[:space:]]*a?wg[[:space:]]+set[[:space:]]+%i[[:space:]]+private-key[[:space:]]+([^[:space:]\;]+)[[:space:]]*$ ]]; then
        die "Ключ сервера подставляется сложной строкой PostUp — перенесите его в PrivateKey вручную (см. SERVER.md)"
    fi
    kf=${BASH_REMATCH[1]}
    [ -f "$kf" ] || die "Файл ключа $kf не найден"
    cp "$f" "$f.bak.$(date +%Y%m%d-%H%M%S)"
    awk -v key="$(cat "$kf")" '
        /^[ \t]*PostUp[ \t]*=.*private-key/ { next }
        { print }
        /^[ \t]*\[Interface\]/ && !done { print "PrivateKey = " key; done = 1 }' "$f" >"$f.tmp"
    mv "$f.tmp" "$f"; chmod 600 "$f"
    ok "Ключ сервера перенесён в $f строкой PrivateKey (копия рядом, .bak)"
}

# ---------- устройства ----------

new_client_conf() { # iface dev label ip priv full
    local iface=$1 dev=$2 label=$3 ip=$4 priv=$5 full=$6 out
    out="$CLIENTS/$iface-$dev.conf"
    mkdir -p "$CLIENTS"; chmod 700 "$STATE" "$CLIENTS"
    local routes; routes="$(server_ip "$iface" | cut -d. -f1-3).0/24"
    [ "$full" = 1 ] && routes="0.0.0.0/0, ::/0"
    (
        umask 077
        {
            echo "# WgSwitch name=$label ping=$(server_ip "$iface")"
            echo "[Interface]"
            echo "PrivateKey = $priv"
            echo "Address = $ip/32"
            [ "$full" = 1 ] && echo "DNS = 1.1.1.1, 1.0.0.1"
            is_awg "$iface" && awg_params "$iface"
            echo
            echo "[Peer]"
            echo "PublicKey = $(server_pub "$iface")"
            echo "AllowedIPs = $routes"
            echo "Endpoint = $(endpoint "$iface"):$(iface_get "$iface" ListenPort)"
            echo "PersistentKeepalive = 25"
        } >"$out"
    )
    echo "$out"
}

# пир — сразу в работающий интерфейс и в файл; остальные устройства не переподключаются
add_peer() { # iface dev pub ip
    local iface=$1 dev=$2 pub=$3 ip=$4 t
    t=$(tool "$iface")
    printf '\n[Peer]\n# %s\nPublicKey = %s\nAllowedIPs = %s/32\n' "$dev" "$pub" "$ip" >>"$(conf_path "$iface")"
    if "$t" show "$iface" >/dev/null 2>&1; then "$t" set "$iface" peer "$pub" allowed-ips "$ip/32"; fi
}

ask_device_name() {
    ask "Имя устройства (для себя: phone, laptop…)" "${1:-phone}"
    [[ $REPLY =~ $NAME_RE ]] || die "Имя: латиница, цифры и _=+.- , до 15 символов"
    grep -qE "^# $REPLY\$" "$(conf_path "$iface")" && die "Устройство «$REPLY» уже есть в $iface"
    dev=$REPLY
}

ask_ip() {
    local free; free=$(next_ip "$iface")
    ask "IP устройства в туннеле" "$free"
    grep -qx "$REPLY" < <(used_ips "$iface") && die "$REPLY уже занят"
    [[ $REPLY =~ ^[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+$ ]] || die "Это не IPv4"
    ip=$REPLY
}

show_client() { # file iface
    local file=$1 iface=$2
    echo
    if command -v qrencode >/dev/null; then
        qrencode -t ansiutf8 <"$file"
        hint "Телефон: WgSwitch или WireGuard → «Сканировать QR-код». Не влезает — уменьшите шрифт терминала."
    else
        warn "qrencode не установлен: apt install qrencode"
    fi
    hint "Для ПК файл: $file"
    hint "  забрать на ПК: scp root@$(endpoint "$iface"):$file ."
    echo
}

cmd_add() {
    local iface dev ip label priv pub full=0 t
    iface=$(pick_iface); t=$(tool "$iface")
    fix_postup_key "$iface"
    ask_device_name
    ask_ip
    label=$(hostname -s | tr -c 'a-zA-Z0-9_=+.\n-' '-' | cut -c1-11)
    is_awg "$iface" && label="$label-awg"
    ask "Как сервер будет называться в WgSwitch" "${label:0:15}"
    [[ $REPLY =~ $NAME_RE ]] || die "Имя: латиница, цифры и _=+.- , до 15 символов"
    label=$REPLY
    if yes_no "Весь интернет устройства через этот сервер? (нужен NAT, см. «Новый туннель»)" n; then full=1; fi
    priv=$("$t" genkey); pub=$(printf '%s\n' "$priv" | "$t" pubkey)
    add_peer "$iface" "$dev" "$pub" "$ip"
    local file; file=$(new_client_conf "$iface" "$dev" "$label" "$ip" "$priv" "$full")
    unset priv
    ok "«$dev» добавлен в $iface ($ip). Остальные устройства не переподключались."
    show_client "$file" "$iface"
}

# ключ устройства создан на нём самом (мастер WgSwitch на ПК) — серверу нужен только публичный
cmd_add_key() {
    local iface dev ip pub
    iface=$(pick_iface)
    fix_postup_key "$iface"
    ask "Публичный ключ устройства (из блока [Peer] в WgSwitch)"
    pub=$(printf '%s' "$REPLY" | sed 's/^[[:space:]]*PublicKey[[:space:]]*=[[:space:]]*//; s/[[:space:]]//g')
    [[ $pub =~ ^[A-Za-z0-9+/]{42}[AEIMQUYcgkosw048]=$ ]] || die "Это не ключ WireGuard (44 символа, в конце =)"
    grep -qF "$pub" "$(conf_path "$iface")" && die "Этот ключ уже есть в $iface"
    ask_device_name pc
    ask_ip
    add_peer "$iface" "$dev" "$pub" "$ip"
    ok "«$dev» добавлен в $iface ($ip)."
    echo
    echo "  В мастере WgSwitch:"
    echo "    Адрес сервера          $(endpoint "$iface"):$(iface_get "$iface" ListenPort)"
    echo "    Публичный ключ сервера $(server_pub "$iface")"
    echo "    IP в туннеле           $ip"
    is_awg "$iface" && warn "Это AmneziaWG: параметры маскировки мастер на ПК пока не принимает — для ПК удобнее «Добавить устройство» и файл .conf"
    echo
}

# имя<TAB>ключ<TAB>адрес для каждого [Peer]
peers() {
    awk '
        function val(s) { s = substr(s, index(s, "=") + 1); gsub(/^[ \t]+|[ \t]+$/, "", s); return s }
        /^[ \t]*\[/ { if (inpeer && pub != "") print name "\t" pub "\t" ip; inpeer = tolower($0) ~ /peer/; name = pub = ip = ""; next }
        inpeer && /^[ \t]*#/ { if (name == "") { n = $0; sub(/^[ \t]*#[ \t]*/, "", n); name = n } next }
        inpeer && tolower($0) ~ /^[ \t]*publickey/ { pub = val($0) }
        inpeer && tolower($0) ~ /^[ \t]*allowedips/ { ip = val($0) }
        END { if (inpeer && pub != "") print name "\t" pub "\t" ip }' "$(conf_path "$1")"
}

ago() {
    local s=$1
    if [ "$s" -le 0 ]; then echo "не подключалось"; return; fi
    s=$(( $(date +%s) - s ))
    if [ $s -lt 60 ]; then echo "$s с назад"; elif [ $s -lt 3600 ]; then echo "$((s / 60)) мин назад"
    elif [ $s -lt 86400 ]; then echo "$((s / 3600)) ч назад"; else echo "$((s / 86400)) д назад"; fi
}

cmd_list() {
    local iface t hs name pub ip any=0
    for iface in $(interfaces); do
        any=1; t=$(tool "$iface")
        printf '\n%s%s%s — %s, порт %s, сервер %s\n' "$B" "$iface" "$N" "$(engine "$iface")" "$(iface_get "$iface" ListenPort)" "$(server_ip "$iface")"
        hs=$("$t" show "$iface" latest-handshakes 2>/dev/null || true)
        while IFS=$'\t' read -r name pub ip; do
            local when; when=$(awk -v k="$pub" '$1 == k { print $2 }' <<<"$hs")
            printf '  %-16s %-16s %s\n' "${name:-без имени}" "$ip" "$(ago "${when:-0}")"
        done < <(peers "$iface")
    done
    [ $any = 1 ] || warn "Туннелей ещё нет"
    echo
}

pick_peer() {
    iface=$(pick_iface)
    mapfile -t rows < <(peers "$iface")
    [ ${#rows[@]} -gt 0 ] || die "В $iface нет устройств"
    local i
    for i in "${!rows[@]}"; do
        IFS=$'\t' read -r name pub ip <<<"${rows[$i]}"
        printf '  %s) %-16s %s\n' $((i + 1)) "${name:-без имени}" "$ip"
    done
    ask "Номер устройства"
    [[ $REPLY =~ ^[0-9]+$ ]] && [ "$REPLY" -ge 1 ] && [ "$REPLY" -le ${#rows[@]} ] || die "Нет такого номера"
    IFS=$'\t' read -r name pub ip <<<"${rows[$((REPLY - 1))]}"
}

cmd_qr() {
    local iface name pub ip rows
    pick_peer
    local file="$CLIENTS/$iface-$name.conf"
    [ -f "$file" ] || die "Для «$name» нет сохранённого конфига: ключ создавался не здесь. Добавьте устройство заново."
    show_client "$file" "$iface"
}

cmd_remove() {
    local iface name pub ip rows t f
    pick_peer
    yes_no "Удалить «${name:-без имени}» ($ip)? Он сразу потеряет доступ" n || { echo "Отменено"; return; }
    t=$(tool "$iface"); f=$(conf_path "$iface")
    cp "$f" "$f.bak.$(date +%Y%m%d-%H%M%S)"
    awk -v k="$pub" '
        function flush() { if (blk != "" && index(blk, k) == 0) printf "%s", blk; blk = "" }
        /^[ \t]*\[/ { flush(); inpeer = tolower($0) ~ /peer/ }
        inpeer { blk = blk $0 "\n"; next }
        { print }
        END { flush() }' "$f" | cat -s >"$f.tmp"
    mv "$f.tmp" "$f"; chmod 600 "$f"
    "$t" set "$iface" peer "$pub" remove 2>/dev/null || true
    rm -f "$CLIENTS/$iface-$name.conf"
    ok "«${name:-без имени}» удалён из $iface"
}

# ---------- новый туннель ----------

free_subnet() {
    local x used
    used=$(for i in $(interfaces); do server_ip "$i"; done)
    for x in $(seq 8 250); do grep -q "^10\.$x\." <<<"$used" || { echo "10.$x.0"; return; }; done
    die "Не нашлось свободной подсети 10.x.0.0/24"
}

free_port() {
    local p used
    used=$(for i in $(interfaces); do iface_get "$i" ListenPort; done; ss -Hlun 2>/dev/null | awk '{print $4}' | sed 's/.*://')
    for p in $(seq "$1" $(($1 + 50))); do grep -qx "$p" <<<"$used" || { echo "$p"; return; }; done
}

free_iface() { local n=0; while [ -f "$(conf_path "$1$n")" ]; do n=$((n + 1)); done; echo "$1$n"; }

# ---------- установка с видимым прогрессом: apt молчит минутами, а сборка модуля ядра — тем более ----------

LOG_FILE=""
STEP=0 STEPS=0

# step "Что делаю" команда… — вывод команды уходит в журнал, на экране — номер шага, индикатор и секунды
step() {
    local msg=$1; shift
    STEP=$((STEP + 1))
    local label="[$STEP/$STEPS] $msg" start=$SECONDS pid i=0 frames='|/-\'
    printf '
=== %s
' "$label" >>"$LOG_FILE"
    ("$@") >>"$LOG_FILE" 2>&1 &
    pid=$!
    if [ -t 1 ]; then
        while kill -0 "$pid" 2>/dev/null; do
            printf '%s %s %s(%d с)%s ' "${frames:i++%4:1}" "$label" "$D" $((SECONDS - start)) "$N"
            sleep 0.25
        done
    else
        printf '… %s
' "$label"
    fi
    if wait "$pid"; then
        printf '%s✓%s %s %s(%d с)%s[K
' "$G" "$N" "$label" "$D" $((SECONDS - start)) "$N"
    else
        printf '%s✗%s %s[K
' "$R" "$N" "$label"
        echo "--- последние строки журнала:" >&2
        tail -n 15 "$LOG_FILE" >&2
        [ -n "${FAIL_HINT:-}" ] && warn "$FAIL_HINT"
        die "Шаг не получился: $msg. Полный журнал: $LOG_FILE"
    fi
}

install_engine() {
    . /etc/os-release
    export DEBIAN_FRONTEND=noninteractive
    mkdir -p "$STATE"; LOG_FILE=$STATE/install.log; : >"$LOG_FILE"
    STEP=0
    local name; name=$([ "$1" = awg ] && echo AmneziaWG || echo WireGuard)
    if [ "$1" = awg ] && command -v awg >/dev/null; then ok "AmneziaWG уже установлен"; return; fi
    if [ "$1" = wg ] && command -v wg >/dev/null && command -v qrencode >/dev/null; then ok "WireGuard уже установлен"; return; fi
    [ "$1" = awg ] && [ "$ID" != ubuntu ] && die "AmneziaWG ставится этим скриптом только на Ubuntu (у вас $PRETTY_NAME)"

    STEPS=$([ "$1" = awg ] && echo 6 || echo 3)
    echo "Ставлю $name — $STEPS шагов. Журнал: $LOG_FILE"
    step "Обновляю список пакетов" apt-get update -q
    step "Ставлю qrencode и утилиты" apt-get install -y -q qrencode curl iproute2
    if [ "$1" = awg ]; then
        step "Ставлю заголовки ядра $(uname -r)" apt-get install -y -q software-properties-common "linux-headers-$(uname -r)"
        step "Подключаю репозиторий AmneziaWG" add-apt-repository -y ppa:amnezia/ppa
        hint "  Дальше модуль собирается под ваше ядро — обычно 1–5 минут, на слабом сервере дольше. Это не зависание."
        step "Ставлю AmneziaWG и собираю модуль ядра" apt-get install -y -q amneziawg amneziawg-tools
        FAIL_HINT="DKMS не собрал модуль под ядро $(uname -r). Обновите ядро: apt full-upgrade -y && reboot — и повторите «Новый туннель»" \
            step "Загружаю модуль ядра" modprobe amneziawg
    else
        step "Ставлю wireguard-tools" apt-get install -y -q wireguard-tools
    fi
    ok "$name установлен"
}

cmd_new() {
    local kind iface port net key nat=0 wan f
    echo
    echo "  1) AmneziaWG — WireGuard с маскировкой: работает там, где провайдер режет WireGuard (рекомендуется в РФ)"
    echo "  2) WireGuard — обычный"
    ask "Движок" 1
    case $REPLY in 1) kind=awg ;; 2) kind=wg ;; *) die "Нет такого варианта" ;; esac
    install_engine "$kind"
    iface=$(free_iface "$kind")
    port=$(free_port $([ $kind = awg ] && echo 51821 || echo 51820))
    net=$(free_subnet)
    ask "Порт UDP" "$port"; port=$REPLY
    ask "Подсеть туннеля (сервер получит .1)" "$net.0/24"; net=${REPLY%.*}
    if yes_no "Пускать через сервер весь интернет устройств (NAT)? Нужно для «весь трафик через VPN»" n; then nat=1; fi

    f=$(conf_path "$iface"); mkdir -p "$(dirname "$f")"
    key=$("$(tool "$iface")" genkey)
    (
        umask 077
        {
            echo "[Interface]"
            echo "Address = $net.1/24"
            echo "ListenPort = $port"
            echo "PrivateKey = $key"
            if [ $kind = awg ]; then
                local s1 s2 h
                s1=$(shuf -i 15-150 -n 1); s2=$(shuf -i 15-150 -n 1)
                while [ $((s1 + 56)) -eq "$s2" ]; do s2=$(shuf -i 15-150 -n 1); done
                mapfile -t h < <(shuf -i 5-2147483647 -n 4)
                printf 'Jc = 4\nJmin = 40\nJmax = 70\nS1 = %s\nS2 = %s\nH1 = %s\nH2 = %s\nH3 = %s\nH4 = %s\n' "$s1" "$s2" "${h[@]}"
            fi
            if [ $nat = 1 ]; then
                wan=$(ip -4 route show default | awk '{for (i = 1; i < NF; i++) if ($i == "dev") { print $(i + 1); exit }}')
                echo "PostUp = iptables -t nat -A POSTROUTING -s $net.0/24 -o $wan -j MASQUERADE"
                echo "PostDown = iptables -t nat -D POSTROUTING -s $net.0/24 -o $wan -j MASQUERADE"
            fi
        } >"$f"
    )
    unset key
    if [ $nat = 1 ]; then
        mkdir -p "$ETC/sysctl.d"
        echo 'net.ipv4.ip_forward = 1' >"$ETC/sysctl.d/99-wgswitch.conf"
        sysctl -q -p "$ETC/sysctl.d/99-wgswitch.conf"
    fi
    if command -v ufw >/dev/null && ufw status | grep -q 'Status: active'; then
        ufw allow "$port/udp" >/dev/null && ufw allow in on "$iface" >/dev/null
        [ $nat = 1 ] && ufw route allow in on "$iface" >/dev/null
        ok "ufw: открыт $port/udp и трафик из $iface"
    fi
    systemctl enable --now "$(service "$iface")" >/dev/null 2>&1 || die "Туннель не запустился: journalctl -u $(service "$iface")"
    ok "$iface поднят: $(engine "$iface"), порт $port, сервер $net.1"
    hint "Если у хостера есть свой файрвол в панели — откройте там $port/udp."
    endpoint "$iface" >/dev/null
    if yes_no "Добавить первое устройство?" y; then cmd_add; fi
}

# ---------- меню ----------

menu() {
    while true; do
        printf '\n%sWgSwitch · сервер%s\n' "$B" "$N"
        echo "  1) Добавить устройство — QR для телефона, файл для ПК"
        echo "  2) Добавить устройство по ключу из WgSwitch на ПК"
        echo "  3) Список устройств"
        echo "  4) Показать QR устройства снова"
        echo "  5) Удалить устройство"
        echo "  6) Новый туннель (AmneziaWG или WireGuard)"
        echo "  0) Выход"
        ask "Что сделать" 1
        case $REPLY in
            1) cmd_add ;; 2) cmd_add_key ;; 3) cmd_list ;; 4) cmd_qr ;; 5) cmd_remove ;; 6) cmd_new ;;
            0 | q | exit) return ;;
            *) warn "Нет такого пункта" ;;
        esac
    done
}

main() {
    need_root
    exec 3<"$TTY" || die "Нет терминала для вопросов: запустите из обычной SSH-сессии"
    case ${1-} in
        add) cmd_add ;; add-key) cmd_add_key ;; list) cmd_list ;; qr) cmd_qr ;; remove) cmd_remove ;; new) cmd_new ;;
        "") if [ -z "$(interfaces)" ]; then echo "Туннелей ещё нет — создадим первый."; cmd_new; fi; menu ;;
        *) echo "Использование: wgswitch-server [add|add-key|list|qr|remove|new]"; exit 1 ;;
    esac
}

[ "${WGS_SOURCE_ONLY:-}" = 1 ] || main "$@"
