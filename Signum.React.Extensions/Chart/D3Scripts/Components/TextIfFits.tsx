import * as React from 'react'

export interface TextIfFitsProps extends React.SVGProps<SVGTextElement>{
  maxWidth: number;
  padding?: number;
  etcText?: string;
  onFit?: (() => React.SVGProps<SVGTextElement>);
  onNoFit?: (() => React.SVGProps<SVGTextElement>);
}

export default function TextIfFits({ maxWidth, padding, children, etcText, onFit, onNoFit, ...atts } :  TextIfFitsProps) {

  const txt = React.useRef<SVGTextElement>(null);
  const [fit, setFit] = React.useState<boolean>(true);

  React.useEffect(() => {
    var width = maxWidth;
    if (padding)
      width -= padding * 2;

    let txtElem = txt.current!;
    txtElem.textContent = getString(children);
    let textLength = txtElem.getComputedTextLength();
    console.log("Width:", width, " textLength:", textLength, " text: ", txtElem.textContent);
    if (textLength > width)
      setFit(false);
  }, [maxWidth, padding, etcText, getString(children)]);

  return (
    <text ref={txt} {...atts} {...(fit ? onFit?.() : onNoFit?.())}>
      {children ?? ""}
    </text>
  );
}

function getString(children: React.ReactNode) {
  return React.Children.toArray(children)[0] as string;
}
