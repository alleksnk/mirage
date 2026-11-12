package main

import (
	"net"
	"strings"
	"testing"
)

func TestFilterHostlistExcludesRootGoogleApisAndGitHub(t *testing.T) {
	raw := `
# comment
youtube.com
googleapis.com
youtubei.googleapis.com
github.com
rutor.info
RUTOR.INFO
https://medium.com/
`
	got := FilterHostlistLines(raw)
	want := map[string]bool{
		"youtube.com":             true,
		"youtubei.googleapis.com": true,
		"rutor.info":              true,
		"medium.com":              true,
	}
	if len(got) != len(want) {
		t.Fatalf("expected %d domains, got %d (%v)", len(want), len(got), got)
	}
	for _, d := range got {
		if !want[d] {
			t.Fatalf("unexpected domain: %s", d)
		}
	}
}

func TestBuildPacScriptFormat(t *testing.T) {
	pac := BuildPacScript([]string{"youtube.com", "rutor.info"}, 1085)
	if !strings.Contains(pac, "PROXY 127.0.0.1:1085") {
		t.Fatalf("PAC missing proxy port")
	}
	if !strings.Contains(pac, "\"youtube.com\"") || !strings.Contains(pac, "\"rutor.info\"") {
		t.Fatalf("PAC missing domains")
	}
}

func TestDnsQueryAndParseAResponse(t *testing.T) {
	q := BuildDnsQuery("youtube.com", 1)
	if len(q) < 17 {
		t.Fatalf("invalid query length %d", len(q))
	}
	// Construct response echoing the question + 1 compressed-name A answer (142.250.74.78)
	resp := make([]byte, len(q))
	copy(resp, q)
	resp[2] = 0x81
	resp[3] = 0x80
	resp[6] = 0x00
	resp[7] = 0x01 // ANCOUNT=1
	ans := []byte{
		0xC0, 0x0C, // pointer to QNAME at offset 12
		0x00, 0x01, // TYPE A
		0x00, 0x01, // CLASS IN
		0x00, 0x00, 0x00, 0x3C, // TTL 60
		0x00, 0x04, // RDLEN 4
		142, 250, 74, 78,
	}
	resp = append(resp, ans...)
	ip := ParseDnsAResponse(resp)
	if ip == nil || !ip.Equal(net.IPv4(142, 250, 74, 78)) {
		t.Fatalf("expected 142.250.74.78, got %v", ip)
	}
}

func TestPopularResourcesHasEightEntries(t *testing.T) {
	if len(PopularResources) != 8 {
		t.Fatalf("expected 8 popular resources, got %d", len(PopularResources))
	}
	seen := make(map[string]bool)
	for _, r := range PopularResources {
		seen[r.ID] = true
	}
	if len(seen) != 8 {
		t.Fatalf("duplicate IDs in PopularResources")
	}
}

