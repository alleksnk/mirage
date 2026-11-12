package main

import (
	"bytes"
	"encoding/binary"
	"flag"
	"fmt"
	"io/fs"
	"log"
	"os"
	"path/filepath"
	"time"

	"go.digitalxero.dev/osx/app"
	"go.digitalxero.dev/osx/dmg"
	"go.digitalxero.dev/osx/sign"
)

func main() {
	amd64Path := flag.String("amd64", "", "path to darwin/amd64 binary")
	arm64Path := flag.String("arm64", "", "path to darwin/arm64 binary")
	tpwsPath := flag.String("tpws", "assets/tpws", "path to universal tpws binary")
	icnsPath := flag.String("icns", "assets/Mirage.icns", "path to Mirage.icns")
	sitesPath := flag.String("sites", "assets/sites.txt", "path to default sites.txt")
	outDmg := flag.String("out", "../release/macos/Mirage-macOS-v1.1.0.dmg", "output dmg path")
	flag.Parse()

	if *amd64Path == "" || *arm64Path == "" {
		log.Fatal("both -amd64 and -arm64 binaries are required")
	}

	amd64Bytes, err := os.ReadFile(*amd64Path)
	if err != nil {
		log.Fatalf("read amd64: %v", err)
	}
	arm64Bytes, err := os.ReadFile(*arm64Path)
	if err != nil {
		log.Fatalf("read arm64: %v", err)
	}
	tpwsBytes, err := os.ReadFile(*tpwsPath)
	if err != nil {
		log.Fatalf("read tpws: %v", err)
	}
	sitesBytes, err := os.ReadFile(*sitesPath)
	if err != nil {
		log.Fatalf("read sites: %v", err)
	}

	universalBin := buildUniversalMachO(amd64Bytes, arm64Bytes)
	buildTime := time.Now().UTC().Truncate(time.Second)

	signer, err := sign.NewCodeSigner().WithAdHoc().Build()
	if err != nil {
		log.Fatalf("create ad-hoc signer: %v", err)
	}
	bundle, err := app.NewBundle().
		WithName("Mirage").
		WithBundleID("com.mirage.dpi").
		WithVersion("1.1.0").
		WithMinimumSystemVersion("11.0").
		WithModTime(buildTime).
		WithExecutable("Mirage", universalBin).
		WithIconFromPath("Mirage.icns", *icnsPath).
		WithResource("sites.txt", sitesBytes).
		WithInfoPlistValue("CFBundleShortVersionString", "1.1.0").
		WithInfoPlistValue("CFBundleDisplayName", "Mirage").
		WithInfoPlistValue("LSApplicationCategoryType", "public.app-category.utilities").
		WithInfoPlistValue("LSUIElement", true).
		File("Contents/MacOS/tpws").WithBody(tpwsBytes).WithMode(0o755).Add().
		WithCodeSigner(signer).
		Build()
	if err != nil {
		log.Fatalf("build signed app bundle: %v", err)
	}

	if err := os.MkdirAll(filepath.Dir(*outDmg), 0o755); err != nil {
		log.Fatalf("mkdir out: %v", err)
	}

	ib := dmg.NewImage().
		WithName("Mirage").
		WithVolumeName("Mirage").
		WithCompression(dmg.Zlib).
		WithCreated(buildTime).
		WithTempDir(filepath.Dir(*outDmg))
	if err := ib.AddTree(bundle.FS(), "/Mirage.app"); err != nil {
		log.Fatalf("add app tree to dmg: %v", err)
	}
	img, err := ib.
		AddSymlink("/Applications", "/Applications").
		Build()
	if err != nil {
		log.Fatalf("build dmg: %v", err)
	}

	f, err := os.Create(*outDmg)
	if err != nil {
		log.Fatalf("create dmg file: %v", err)
	}
	if _, err := img.WriteTo(f); err != nil {
		_ = f.Close()
		log.Fatalf("write dmg: %v", err)
	}
	if err := f.Close(); err != nil {
		log.Fatalf("close dmg: %v", err)
	}

	verifyDmg(*outDmg)
	fmt.Printf("DMG_BUILT_AND_VERIFIED: %s\n", *outDmg)
}

func buildUniversalMachO(amd64Slice, arm64Slice []byte) []byte {
	const (
		fatMagic     = 0xCAFEBABE
		cpuTypeX64   = 0x01000007
		cpuSubX64    = 3
		cpuTypeArm64 = 0x0100000C
		cpuSubArm64  = 0
		alignPow     = 14 // 16384 bytes (required by Apple Silicon)
		pageSize     = 1 << alignPow
	)

	alignUp := func(n int) int {
		rem := n % pageSize
		if rem == 0 {
			return n
		}
		return n + (pageSize - rem)
	}

	off0 := alignUp(8 + 2*20)
	off1 := alignUp(off0 + len(amd64Slice))
	totalSize := off1 + len(arm64Slice)

	var buf bytes.Buffer
	buf.Grow(totalSize)

	writeBE32 := func(v uint32) {
		_ = binary.Write(&buf, binary.BigEndian, v)
	}
	// Fat header
	writeBE32(fatMagic)
	writeBE32(2)

	// Slice 0: x86_64
	writeBE32(cpuTypeX64)
	writeBE32(cpuSubX64)
	writeBE32(uint32(off0))
	writeBE32(uint32(len(amd64Slice)))
	writeBE32(alignPow)

	// Slice 1: arm64
	writeBE32(cpuTypeArm64)
	writeBE32(cpuSubArm64)
	writeBE32(uint32(off1))
	writeBE32(uint32(len(arm64Slice)))
	writeBE32(alignPow)

	for buf.Len() < off0 {
		buf.WriteByte(0)
	}
	buf.Write(amd64Slice)
	for buf.Len() < off1 {
		buf.WriteByte(0)
	}
	buf.Write(arm64Slice)
	return buf.Bytes()
}

func verifyDmg(path string) {
	f, err := os.Open(path)
	if err != nil {
		log.Fatalf("open dmg for verify: %v", err)
	}
	defer f.Close()
	st, err := f.Stat()
	if err != nil {
		log.Fatalf("stat dmg: %v", err)
	}
	r, err := dmg.Open(f, st.Size())
	if err != nil {
		log.Fatalf("dmg.Open failed: %v", err)
	}
	if err := r.Verify(); err != nil {
		log.Fatalf("dmg.Verify checksum failed: %v", err)
	}

	fsys := r.FS()
	mustExec := []string{
		"Mirage.app/Contents/MacOS/Mirage",
		"Mirage.app/Contents/MacOS/tpws",
	}
	for _, p := range mustExec {
		info, err := fs.Stat(fsys, p)
		if err != nil {
			log.Fatalf("missing inside dmg: %s (%v)", p, err)
		}
		if info.Mode()&0o111 == 0 {
			log.Fatalf("file %s lacks +x perm: %v", p, info.Mode())
		}
	}
	mustExist := []string{
		"Mirage.app/Contents/Info.plist",
		"Mirage.app/Contents/Resources/Mirage.icns",
		"Mirage.app/Contents/_CodeSignature/CodeResources",
		"Applications",
	}
	for _, p := range mustExist {
		if _, err := fs.Stat(fsys, p); err != nil {
			if rl, ok := fsys.(fs.ReadLinkFS); ok {
				if _, lerr := rl.ReadLink(p); lerr == nil {
					continue
				}
			}
			log.Fatalf("missing required entry inside dmg: %s (%v)", p, err)
		}
	}
}
