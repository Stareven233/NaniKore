package version

import (
	_ "embed"
	"fmt"
	"strings"
)

//go:embed config.yaml
var configFile string

var (
	Version string
	Build   string
	Full    string
)

func init() {
	// Parse config.yaml
	lines := strings.SplitSeq(configFile, "\n")
	for line := range lines {
		line = strings.TrimSpace(line)
		if after, ok := strings.CutPrefix(line, "version:"); ok {
			Version = strings.TrimSpace(after)
		} else if after0, ok0 := strings.CutPrefix(line, "build:"); ok0 {
			Build = strings.TrimSpace(after0)
		}
	}

	if Build == "0" {
		Full = Version
	} else {
		Full = fmt.Sprintf("%s+%s", Version, Build)
	}
}

// String returns the full version string
func String() string {
	return Full
}
